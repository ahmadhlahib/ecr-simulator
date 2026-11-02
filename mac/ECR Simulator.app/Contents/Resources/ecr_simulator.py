"""ECR Simulator for macOS - the window. Created by Ahmad El Lahib.

Run: python3 ecr_simulator.py   (or double-click "ECR Simulator.command")
Needs Python 3.9+ with Tk 8.6 (the python.org installer, or Homebrew python + python-tk).
"""

import json
import os
import re
import subprocess
import sys
import tkinter as tk
from decimal import Decimal, InvalidOperation
from tkinter import messagebox, ttk
from tkinter.scrolledtext import ScrolledText

import ecr_engine as E
import mac_port

VERSION = "2.1"
AUTHOR = "Ahmad El Lahib"
HERE = os.path.dirname(os.path.abspath(__file__))
# Logs and settings: ~/Documents/ECR Simulator when started from the .app (the launcher sets ECR_SIM_DATA), else here.
DATA = os.environ.get("ECR_SIM_DATA") or HERE
SETTINGS_FILE = os.path.join(DATA, "ecr-simulator.json")

CABLES = (
    "Nexgo D20 Bluetooth base (USB)  -  115200, RTS 0, DTR 0",
    "Direct cable to the terminal  -  9600, RTS 1, DTR 1",
    "Custom",
)
FORMATS = ("QS (query string)", "HDMC", "KV (direct)", "KV Jordan (progressive + delivery ack)")
CURRENCIES = ("840 USD", "422 LBP", "400 JOD", "368 IQD", "978 EUR", "784 AED", "682 SAR", "818 EGP")
FRAMINGS = (
    "As typed",
    "QS register frame: STX text ETX LRC",
    "POS style: STX text ETX CR LF",
    'KV frame: "<START>","field",...,"<END>"',
)
LINES = ("0 (off)", "1 (on)", "driver")
CTRL_NAMES = ("NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "TAB", "LF", "VT", "FF", "CR", "SO", "SI",
              "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US")

ACCENT = "#005caf"
LOG_TAGS = {"PC->POS": "tx", "POS->PC": "rx", "!!": "err", "==": "res", "hex": "hex", "--": "info"}
OK = "#16803c"
BAD = "#be1e1e"


def unescape(s):
    def ctrl(m):
        n = m.group(1).upper()
        if n in CTRL_NAMES:
            return chr(CTRL_NAMES.index(n))
        if re.fullmatch(r"[0-9A-F]{2}", n):
            return chr(int(n, 16))
        return m.group(0)
    s = re.sub(r"<([A-Za-z]{2,3}|[0-9A-Fa-f]{2})>", ctrl, s)
    s = re.sub(r"\\x([0-9A-Fa-f]{2})", lambda m: chr(int(m.group(1), 16)), s)
    return s.replace("\\r", "\r").replace("\\n", "\n").replace("\\t", "\t")


def build_free_text(text, framing, escapes, hex_in, crlf):
    """The bytes (as a latin-1 string) the free text box sends."""
    if hex_in:
        h = re.sub(r"0x|[\s,;:-]", "", text, flags=re.I)
        if len(h) % 2 or not re.fullmatch(r"[0-9a-fA-F]*", h):
            raise ValueError("Hex input must be pairs of 0-9 / A-F, like 02 41 03.")
        body = "".join(chr(int(h[i:i + 2], 16)) for i in range(0, len(h), 2))
    else:
        body = text.replace("\r\n", "\n")
        if escapes:
            body = unescape(body)
    if framing == 1:
        body = E.qs_frame(body)
    elif framing == 2:
        body = E.STX + body + E.ETX + "\r\n"
    elif framing == 3:
        parts = [p.strip().strip('"') for p in re.split(r"[,\n]", body)]
        body = E.kv_frame([p for p in parts if p])
    if crlf:
        body += "\r\n"
    return body


class App:
    def __init__(self, root, port_arg=None):
        self.root = root
        self.cfg = self._load()
        self.loading = True
        self.shown_version = -1
        self.seen_sale = self.seen_preauth = self.seen_sent = None
        self.history = list(self.cfg.get("history", []))
        self.engine = E.Engine(mac_port.PosixSerial, mac_port.port_exists, os.path.join(DATA, "logs"))

        root.title("ECR Simulator %s  -  by %s" % (VERSION, AUTHOR))
        try:
            self.icon = tk.PhotoImage(file=os.path.join(HERE, "ecr.png"))
            root.iconphoto(True, self.icon)   # Dock icon on macOS
        except tk.TclError:
            pass
        w, h = min(1320, root.winfo_screenwidth()), min(900, root.winfo_screenheight() - 60)
        root.geometry("%dx%d" % (w, h))
        root.minsize(980, 620)
        self._build()
        self._refresh_ports(port_arg or self.cfg.get("port", ""))
        self._apply_saved()
        self.loading = False
        self._push()
        self.engine.start()
        root.protocol("WM_DELETE_WINDOW", self._on_close)
        root.after(100, self._poll)

    # ===================== layout =====================

    def _build(self):
        r = self.root
        header = tk.Frame(r, bg=ACCENT)
        header.pack(side="top", fill="x")
        tk.Label(header, text="ECR Simulator", bg=ACCENT, fg="white", font=("Helvetica", 20, "bold")).pack(side="left", padx=12, pady=6)
        tk.Label(header, text="Cash register for the Areeba POS ECR link  -  QS, HDMC, KV, KV Jordan  -  by " + AUTHOR,
                 bg=ACCENT, fg="#dce8f5").pack(side="left", padx=4)
        ttk.Button(header, text="About", command=self._about).pack(side="right", padx=12)

        body = ttk.Frame(r)
        body.pack(fill="both", expand=True)

        # Left column scrolls on small screens.
        lc = tk.Canvas(body, width=470, highlightthickness=0)
        sb = ttk.Scrollbar(body, orient="vertical", command=lc.yview)
        left = ttk.Frame(lc, padding=(8, 6, 4, 8))
        left.bind("<Configure>", lambda e: lc.configure(scrollregion=lc.bbox("all"), width=left.winfo_reqwidth()))
        lc.create_window((0, 0), window=left, anchor="nw")
        lc.configure(yscrollcommand=sb.set)
        lc.pack(side="left", fill="y")
        sb.pack(side="left", fill="y")
        lc.bind_all("<MouseWheel>", lambda e: lc.yview_scroll(int(-e.delta), "units") if e.widget.winfo_toplevel() == r and str(e.widget).startswith(str(lc)) else None)

        right = ttk.Frame(body, padding=(4, 6, 8, 8))
        right.pack(side="left", fill="both", expand=True)

        self._build_connection(left)
        self._build_protocol(left)
        self._build_transaction(left)
        self._build_tests(left)
        self._build_right(right)

    @staticmethod
    def _grid(parent, row, col, widget, **kw):
        widget.grid(row=row, column=col, sticky=kw.pop("sticky", "w"), padx=kw.pop("padx", 3), pady=kw.pop("pady", 3), **kw)
        return widget

    def _combo(self, parent, values, width, editable=False, var=None):
        var = var or tk.StringVar(value=values[0] if values else "")
        c = ttk.Combobox(parent, values=values, width=width, textvariable=var, state="normal" if editable else "readonly")
        return c, var

    def _spin(self, parent, lo, hi, value, width=6):
        var = tk.IntVar(value=value)
        s = ttk.Spinbox(parent, from_=lo, to=hi, width=width, textvariable=var)
        return s, var

    def _build_connection(self, p):
        g = ttk.LabelFrame(p, text="1. Connection (cable)", padding=6)
        g.pack(fill="x", pady=(0, 6))
        G = self._grid
        G(g, 0, 0, ttk.Label(g, text="Port"))
        self.cb_port, self.v_port = self._combo(g, [], 36, editable=True)
        G(g, 0, 1, self.cb_port, columnspan=4, sticky="we")
        G(g, 0, 5, ttk.Button(g, text="Refresh", width=7, command=lambda: self._refresh_ports(self._port_name())))
        G(g, 1, 0, ttk.Label(g, text="Cable"))
        self.cb_cable, self.v_cable = self._combo(g, CABLES, 46)
        G(g, 1, 1, self.cb_cable, columnspan=5, sticky="we")
        G(g, 2, 0, ttk.Label(g, text="Baud"))
        self.cb_baud, self.v_baud = self._combo(g, ("115200", "57600", "38400", "19200", "9600", "4800", "2400"), 8, editable=True)
        G(g, 2, 1, self.cb_baud)
        f = ttk.Frame(g)
        G(g, 2, 2, f, columnspan=4)
        ttk.Label(f, text="Data").pack(side="left")
        self.cb_data, self.v_data = self._combo(f, ("8", "7"), 2)
        self.cb_data.pack(side="left", padx=(2, 8))
        ttk.Label(f, text="Parity").pack(side="left")
        self.cb_parity, self.v_parity = self._combo(f, ("None", "Odd", "Even"), 5)
        self.cb_parity.pack(side="left", padx=(2, 8))
        ttk.Label(f, text="Stop").pack(side="left")
        self.cb_stop, self.v_stop = self._combo(f, ("1", "2"), 2)
        self.cb_stop.pack(side="left", padx=2)
        G(g, 3, 0, ttk.Label(g, text="RTS"))
        self.cb_rts, self.v_rts = self._combo(g, LINES, 8)
        G(g, 3, 1, self.cb_rts)
        f = ttk.Frame(g)
        G(g, 3, 2, f, columnspan=4)
        ttk.Label(f, text="DTR").pack(side="left")
        self.cb_dtr, self.v_dtr = self._combo(f, LINES, 8)
        self.cb_dtr.pack(side="left", padx=4)
        ttk.Label(f, text="D20 base: keep RTS 0", foreground="gray").pack(side="left", padx=4)

        self.v_pertxn = tk.BooleanVar(value=True)
        G(g, 4, 0, ttk.Radiobutton(g, text="Always open  (Connect / Disconnect)", variable=self.v_pertxn, value=False), columnspan=6)
        G(g, 5, 0, ttk.Radiobutton(g, text="Open per transaction  (duty free / Mega Tech: open, sale, result, close)",
                                   variable=self.v_pertxn, value=True), columnspan=6)
        f = ttk.Frame(g)
        G(g, 6, 0, f, columnspan=6)
        ttk.Label(f, text="    Close the port").pack(side="left")
        self.sp_close, self.v_close = self._spin(f, 0, 60000, 0, 7)
        self.sp_close.pack(side="left", padx=4)
        ttk.Label(f, text="ms after the result").pack(side="left")
        f = ttk.Frame(g)
        G(g, 7, 0, f, columnspan=6, sticky="we")
        self.v_reopen = tk.BooleanVar(value=True)
        ttk.Checkbutton(f, text="Auto-reopen a dead port", variable=self.v_reopen).pack(side="left")
        self.lb_status = tk.Label(f, text="CLOSED", bg="gray", fg="white", width=8, font=("Helvetica", 11, "bold"))
        self.lb_status.pack(side="right", padx=2)
        self.bt_disconnect = ttk.Button(f, text="Disconnect", command=self.engine.disconnect)
        self.bt_disconnect.pack(side="right", padx=2)
        self.bt_connect = ttk.Button(f, text="Connect", command=lambda: (self._push(), self.engine.connect()))
        self.bt_connect.pack(side="right", padx=2)

        self.cb_cable.bind("<<ComboboxSelected>>", lambda e: self._apply_cable())
        self.cb_port.bind("<<ComboboxSelected>>", lambda e: self._port_selected())
        for v in (self.v_baud, self.v_data, self.v_parity, self.v_stop, self.v_rts, self.v_dtr):
            v.trace_add("write", lambda *a: self._cable_field_changed())
        for v in (self.v_port, self.v_pertxn, self.v_close, self.v_reopen):
            v.trace_add("write", lambda *a: self._push())

    def _build_protocol(self, p):
        g = ttk.LabelFrame(p, text="2. Protocol", padding=6)
        g.pack(fill="x", pady=(0, 6))
        G = self._grid
        G(g, 0, 0, ttk.Label(g, text="Format"))
        self.cb_format, self.v_format = self._combo(g, FORMATS, 40)
        G(g, 0, 1, self.cb_format, columnspan=5, sticky="we")
        self.v_handshake = tk.BooleanVar(value=False)
        G(g, 1, 0, ttk.Checkbutton(g, text="Connection check before each transaction (IsConnected? / KV keep-alive)",
                                   variable=self.v_handshake), columnspan=6)
        f = ttk.Frame(g)
        G(g, 2, 0, f, columnspan=6)
        ttk.Label(f, text="Tries").pack(side="left")
        self.sp_tries, self.v_tries = self._spin(f, 1, 99, 5, 3)
        self.sp_tries.pack(side="left", padx=(2, 8))
        ttk.Label(f, text="wait").pack(side="left")
        self.sp_oksec, self.v_oksec = self._spin(f, 1, 300, 11, 4)
        self.sp_oksec.pack(side="left", padx=2)
        ttk.Label(f, text="s each    Result timeout").pack(side="left")
        self.sp_result, self.v_result = self._spin(f, 5, 3600, 120, 5)
        self.sp_result.pack(side="left", padx=2)
        ttk.Label(f, text="s").pack(side="left")
        f = ttk.Frame(g)
        G(g, 3, 0, f, columnspan=6)
        ttk.Label(f, text="Auto check every").pack(side="left")
        self.sp_auto, self.v_auto = self._spin(f, 0, 3600, 0, 5)
        self.sp_auto.pack(side="left", padx=2)
        ttk.Label(f, text="s while idle (always-open mode, 0 = off)").pack(side="left")
        f = ttk.Frame(g)
        G(g, 4, 0, f, columnspan=6)
        self.v_kvack = tk.BooleanVar(value=True)
        self.ck_kvack = ttk.Checkbutton(f, text="KV Jordan: delivery ack after an approved sale, delay", variable=self.v_kvack)
        self.ck_kvack.pack(side="left")
        self.sp_ack, self.v_ack = self._spin(f, 0, 10000, 0, 5)
        self.sp_ack.pack(side="left", padx=2)
        ttk.Label(f, text="ms").pack(side="left")
        for v in (self.v_format, self.v_handshake, self.v_tries, self.v_oksec, self.v_result, self.v_auto, self.v_kvack, self.v_ack):
            v.trace_add("write", lambda *a: self._push())

    def _build_transaction(self, p):
        g = ttk.LabelFrame(p, text="3. Transaction", padding=6)
        g.pack(fill="x", pady=(0, 6))
        G = self._grid
        G(g, 0, 0, ttk.Label(g, text="Amount"))
        self.v_amount = tk.StringVar(value="1.00")
        G(g, 0, 1, ttk.Entry(g, textvariable=self.v_amount, width=10))
        self.cb_cur, self.v_cur = self._combo(g, CURRENCIES, 9, editable=True)
        G(g, 0, 2, self.cb_cur)
        f = ttk.Frame(g)
        G(g, 0, 3, f)
        ttk.Label(f, text="Decimals").pack(side="left")
        self.sp_dec, self.v_dec = self._spin(f, 0, 4, 2, 2)
        self.sp_dec.pack(side="left", padx=2)
        G(g, 1, 0, ttk.Label(g, text="ECR ref"))
        self.v_ref = tk.StringVar(value=E.ref8(""))
        G(g, 1, 1, ttk.Entry(g, textvariable=self.v_ref, width=10))
        self.v_autoref = tk.BooleanVar(value=True)
        G(g, 1, 2, ttk.Checkbutton(g, text="+1 after each new sale / preauth / refund", variable=self.v_autoref), columnspan=2)
        G(g, 2, 0, ttk.Label(g, text="Original ref"))
        self.v_orig = tk.StringVar()
        G(g, 2, 1, ttk.Entry(g, textvariable=self.v_orig, width=10))
        f = ttk.Frame(g)
        G(g, 2, 2, f, columnspan=2)
        ttk.Label(f, text="KV TXNID").pack(side="left")
        self.v_txnid = tk.StringVar()
        self.en_txnid = ttk.Entry(f, textvariable=self.v_txnid, width=10)
        self.en_txnid.pack(side="left", padx=4)
        G(g, 3, 0, ttk.Label(g, text="KV seq no"))
        self.v_seq = tk.StringVar()
        self.en_seq = ttk.Entry(g, textvariable=self.v_seq, width=10)
        G(g, 3, 1, self.en_seq)
        f = ttk.Frame(g)
        G(g, 3, 2, f, columnspan=2)
        ttk.Label(f, text="Repeat").pack(side="left")
        self.sp_rep, self.v_rep = self._spin(f, 1, 9999, 1, 4)
        self.sp_rep.pack(side="left", padx=2)
        ttk.Label(f, text="x every").pack(side="left")
        self.sp_repsec, self.v_repsec = self._spin(f, 1, 3600, 10, 4)
        self.sp_repsec.pack(side="left", padx=2)
        ttk.Label(f, text="s").pack(side="left")

        bf = ttk.Frame(g)
        G(g, 4, 0, bf, columnspan=4, sticky="we", pady=(8, 2))
        mk = lambda text, cmd: ttk.Button(bf, text=text, command=cmd, width=13)
        self.bt_sale = mk("Sale", lambda: self._txn("Sale"))
        self.bt_preauth = mk("Preauth", lambda: self._txn("Preauth"))
        self.bt_completion = mk("Completion", lambda: self._txn("Completion"))
        self.bt_refund = mk("Refund", lambda: self._txn("Refund"))
        self.bt_void = mk("Void", lambda: self._txn("Void"))
        self.bt_settle = mk("Settlement", lambda: self._txn("Settlement"))
        self.bt_check = mk("IsConnected?", lambda: (self._push(), self.engine.check()))
        self.bt_stop = mk("Stop waiting", self.engine.stop)
        for i, b in enumerate((self.bt_sale, self.bt_preauth, self.bt_completion, self.bt_refund,
                               self.bt_void, self.bt_settle, self.bt_check, self.bt_stop)):
            b.grid(row=i // 4, column=i % 4, padx=2, pady=3, ipady=4)
        self.lb_last = ttk.Label(g, text="No approved sale yet.", foreground="gray", wraplength=430)
        G(g, 5, 0, self.lb_last, columnspan=4)

    def _build_tests(self, p):
        g = ttk.LabelFrame(p, text="4. Fault tests", padding=6)
        g.pack(fill="x")
        f = ttk.Frame(g)
        f.pack(fill="x")
        ttk.Label(f, text="Stop reading the port for").pack(side="left")
        sp, self.v_pause = self._spin(f, 1, 3600, 60, 5)
        sp.pack(side="left", padx=2)
        ttk.Label(f, text="s").pack(side="left")
        ttk.Button(f, text="Go", width=4, command=lambda: self.engine.pause_reading(self._int(self.v_pause, 60))).pack(side="left", padx=4)
        ttk.Button(f, text="Reset counters", command=self.engine.reset_stats).pack(side="right")
        f = ttk.Frame(g)
        f.pack(fill="x", pady=(4, 0))
        ttk.Label(f, text="Close the port for").pack(side="left")
        sp, self.v_closed = self._spin(f, 1, 3600, 60, 5)
        sp.pack(side="left", padx=2)
        ttk.Label(f, text="s").pack(side="left")
        ttk.Button(f, text="Go", width=4, command=lambda: self.engine.close_port_for(self._int(self.v_closed, 60))).pack(side="left", padx=4)

    def _build_right(self, p):
        disp = tk.Frame(p, bg="#182028", padx=12, pady=6)
        disp.pack(fill="x")
        self.lb_disp1 = tk.Label(disp, text="READY", bg="#182028", fg="#78ffa0", font=("Menlo", 20, "bold"), anchor="w")
        self.lb_disp1.pack(fill="x")
        self.lb_disp2 = tk.Label(disp, text="", bg="#182028", fg="#96dcff", font=("Menlo", 13), anchor="w", justify="left")
        self.lb_disp2.pack(fill="x")

        res = ttk.LabelFrame(p, text="Result", padding=6)
        res.pack(fill="x", pady=6)
        self.lb_res = tk.Label(res, text="", font=("Helvetica", 16, "bold"), anchor="w")
        self.lb_res.pack(fill="x")
        self.tx_res = ScrolledText(res, height=8, font=("Menlo", 11), relief="flat", wrap="word")
        self.tx_res.pack(fill="x")
        self.lb_stats = ttk.Label(res, text="", foreground="gray")
        self.lb_stats.pack(fill="x")

        free = ttk.LabelFrame(p, text="Free text  (send anything on this cable)", padding=6)
        free.pack(fill="x")
        self.tx_free = tk.Text(free, height=3, font=("Menlo", 12), wrap="word")
        self.tx_free.pack(fill="x")
        f = ttk.Frame(free)
        f.pack(fill="x", pady=(4, 0))
        ttk.Label(f, text="Frame").pack(side="left")
        self.cb_framing, self.v_framing = self._combo(f, FRAMINGS, 34)
        self.cb_framing.pack(side="left", padx=4)
        self.v_crlf = tk.BooleanVar(value=False)
        ttk.Checkbutton(f, text="+ CR LF", variable=self.v_crlf).pack(side="left", padx=4)
        f = ttk.Frame(free)
        f.pack(fill="x", pady=(4, 0))
        self.v_escapes = tk.BooleanVar(value=True)
        ttk.Checkbutton(f, text="Escapes  <STX> <ETX> <CR> <LF> <ACK> \\x1C", variable=self.v_escapes).pack(side="left")
        self.v_hexin = tk.BooleanVar(value=False)
        ttk.Checkbutton(f, text="Text is hex bytes (02 41 03)", variable=self.v_hexin).pack(side="left", padx=10)
        f = ttk.Frame(free)
        f.pack(fill="x", pady=(4, 0))
        ttk.Label(f, text="Send").pack(side="left")
        sp, self.v_frep = self._spin(f, 1, 1000, 1, 4)
        sp.pack(side="left", padx=2)
        ttk.Label(f, text="x every").pack(side="left")
        sp, self.v_fint = self._spin(f, 0, 600000, 1000, 7)
        sp.pack(side="left", padx=2)
        ttk.Label(f, text="ms").pack(side="left")
        ttk.Button(f, text="Send", command=self._send_free).pack(side="left", padx=8)
        self.cb_hist, self.v_hist = self._combo(f, [], 30)
        self.cb_hist.pack(side="left", fill="x", expand=True)
        self.cb_hist.bind("<<ComboboxSelected>>", lambda e: self._history_picked())

        lg = ttk.LabelFrame(p, text="Log  (PC->POS = sent by this register, POS->PC = from the terminal)", padding=6)
        lg.pack(fill="both", expand=True, pady=(6, 0))
        f = ttk.Frame(lg)
        f.pack(fill="x")
        self.v_follow = tk.BooleanVar(value=True)
        ttk.Checkbutton(f, text="Follow", variable=self.v_follow).pack(side="left")
        self.v_loghex = tk.BooleanVar(value=False)
        ttk.Checkbutton(f, text="Hex of every read / write", variable=self.v_loghex).pack(side="left", padx=8)
        self.v_loghex.trace_add("write", lambda *a: self._push())
        ttk.Button(f, text="Clear", command=lambda: self.tx_log.delete("1.0", "end")).pack(side="left", padx=2)
        ttk.Button(f, text="Copy all", command=self._copy_log).pack(side="left", padx=2)
        ttk.Button(f, text="Open logs folder", command=self._open_logs).pack(side="left", padx=2)
        ttk.Label(f, text=os.path.basename(self.engine.log_file or ""), foreground="gray").pack(side="left", padx=6)
        self.tx_log = ScrolledText(lg, font=("Menlo", 11), wrap="word", relief="flat", background="#fafbfd")
        self.tx_log.pack(fill="both", expand=True, pady=(4, 0))
        for tag, color in (("tx", "#0050aa"), ("rx", "#00783c"), ("err", BAD), ("res", "#783c00"), ("hex", "#8c8c8c"), ("info", "#5a5a5a")):
            self.tx_log.tag_configure(tag, foreground=color)

    # ===================== settings =====================

    def _load(self):
        try:
            with open(SETTINGS_FILE, encoding="utf-8") as fh:
                return json.load(fh)
        except (OSError, ValueError):
            return {}

    def _save(self):
        s = self._read_settings()
        d = {
            "port": s.port, "cable": self.cb_cable.current(), "baud": self.v_baud.get(), "data": self.v_data.get(),
            "parity": self.v_parity.get(), "stop": self.v_stop.get(), "rts": self.cb_rts.current(), "dtr": self.cb_dtr.current(),
            "perTxn": s.per_txn, "closeDelay": s.close_delay_ms, "autoReopen": s.auto_reopen, "format": self.cb_format.current(),
            "handshake": s.handshake, "tries": s.ok_retries, "okSec": s.ok_timeout_sec, "resultSec": s.result_timeout_sec,
            "autoCheck": s.auto_check_sec, "kvAck": s.kv_ack, "ackDelay": s.kv_ack_delay_ms, "amount": self.v_amount.get(),
            "currency": self.v_cur.get(), "decimals": self._int(self.v_dec, 2), "ref": self.v_ref.get(), "autoRef": self.v_autoref.get(),
            "seqNo": self.v_seq.get(), "repeatSec": self._int(self.v_repsec, 10), "framing": self.cb_framing.current(),
            "escapes": self.v_escapes.get(), "hexIn": self.v_hexin.get(), "crlf": self.v_crlf.get(),
            "freeInterval": self._int(self.v_fint, 1000), "freeText": self.tx_free.get("1.0", "end-1c"),
            "logHex": s.log_hex, "history": self.history,
        }
        try:
            with open(SETTINGS_FILE, "w", encoding="utf-8") as fh:
                json.dump(d, fh, indent=1)
        except OSError:
            pass  # settings are a convenience

    def _apply_saved(self):
        c = self.cfg

        def idx(combo, key, default=0):
            i = c.get(key, default)
            if isinstance(i, int) and 0 <= i < len(combo["values"]):
                combo.current(i)
        idx(self.cb_cable, "cable")
        self.v_baud.set(c.get("baud", "115200"))
        self.v_data.set(c.get("data", "8"))
        self.v_parity.set(c.get("parity", "None"))
        self.v_stop.set(c.get("stop", "1"))
        idx(self.cb_rts, "rts")
        idx(self.cb_dtr, "dtr")
        self.v_pertxn.set(c.get("perTxn", True))
        self.v_close.set(c.get("closeDelay", 0))
        self.v_reopen.set(c.get("autoReopen", True))
        idx(self.cb_format, "format")
        self.v_handshake.set(c.get("handshake", False))
        self.v_tries.set(c.get("tries", 5))
        self.v_oksec.set(c.get("okSec", 11))
        self.v_result.set(c.get("resultSec", 120))
        self.v_auto.set(c.get("autoCheck", 0))
        self.v_kvack.set(c.get("kvAck", True))
        self.v_ack.set(c.get("ackDelay", 0))
        self.v_amount.set(c.get("amount", "1.00"))
        self.v_cur.set(c.get("currency", "840 USD"))
        self.v_dec.set(c.get("decimals", 2))
        self.v_ref.set(c.get("ref", self.v_ref.get()))
        self.v_autoref.set(c.get("autoRef", True))
        self.v_seq.set(c.get("seqNo", ""))
        self.v_repsec.set(c.get("repeatSec", 10))
        idx(self.cb_framing, "framing")
        self.v_escapes.set(c.get("escapes", True))
        self.v_hexin.set(c.get("hexIn", False))
        self.v_crlf.set(c.get("crlf", False))
        self.v_fint.set(c.get("freeInterval", 1000))
        self.tx_free.insert("1.0", c.get("freeText", "IsConnected?"))
        self.v_loghex.set(c.get("logHex", False))
        self._refresh_history()

    @staticmethod
    def _int(var, default):
        try:
            return int(var.get())
        except (tk.TclError, ValueError):
            return default

    def _port_name(self):
        return self.v_port.get().split("  ")[0].strip()

    def _refresh_ports(self, select):
        was = self.loading
        self.loading = True
        ports = mac_port.list_ports()
        labels = ["%s  %s" % (p, l) for p, l in ports]
        self.cb_port["values"] = labels
        match = [l for (p, _), l in zip(ports, labels) if p == select]
        if match:
            self.v_port.set(match[0])
        elif select:
            self.v_port.set(select + "  (not present now)")
        elif labels:
            self.v_port.set(labels[0])
        self.loading = was
        self._push()

    def _port_selected(self):
        if "D20" in self.v_port.get() and self.cb_cable.current() != 0:
            self.cb_cable.current(0)
            self._apply_cable()
        self._push()

    def _apply_cable(self):
        i = self.cb_cable.current()
        self.loading = True
        if i == 0:
            self.v_baud.set("115200")
            self.cb_rts.current(0)
            self.cb_dtr.current(0)
        elif i == 1:
            self.v_baud.set("9600")
            self.cb_rts.current(1)
            self.cb_dtr.current(1)
        if i != 2:
            self.v_data.set("8")
            self.v_parity.set("None")
            self.v_stop.set("1")
        self.loading = False
        self._push()

    def _matches_cable(self, i):
        common = self.v_data.get() == "8" and self.v_parity.get() == "None" and self.v_stop.get() == "1"
        if i == 0:
            return common and self.v_baud.get() == "115200" and self.cb_rts.current() == 0 and self.cb_dtr.current() == 0
        if i == 1:
            return common and self.v_baud.get() == "9600" and self.cb_rts.current() == 1 and self.cb_dtr.current() == 1
        return True

    def _cable_field_changed(self):
        if self.loading:
            return
        if self.cb_cable.current() != 2 and not self._matches_cable(self.cb_cable.current()):
            self.cb_cable.current(2)
        self._push()

    def _fmt(self):
        return E.FORMATS[max(0, self.cb_format.current())]

    def _read_settings(self):
        try:
            baud = int(self.v_baud.get())
        except ValueError:
            baud = 9600
        return E.Settings(
            port=self._port_name(), baud=baud, data_bits=7 if self.v_data.get() == "7" else 8,
            parity={"Odd": "O", "Even": "E"}.get(self.v_parity.get(), "N"), stop_bits=2 if self.v_stop.get() == "2" else 1,
            rts=max(0, self.cb_rts.current()), dtr=max(0, self.cb_dtr.current()), fmt=self._fmt(),
            per_txn=self.v_pertxn.get(), close_delay_ms=self._int(self.v_close, 0), auto_reopen=self.v_reopen.get(),
            handshake=self.v_handshake.get(), ok_retries=self._int(self.v_tries, 5), ok_timeout_sec=self._int(self.v_oksec, 11),
            result_timeout_sec=self._int(self.v_result, 120), auto_check_sec=self._int(self.v_auto, 0),
            kv_ack=self.v_kvack.get(), kv_ack_delay_ms=self._int(self.v_ack, 0), log_hex=self.v_loghex.get(),
        )

    def _push(self):
        if self.loading:
            return
        self.engine.apply(self._read_settings())
        self._update_buttons(self.engine.snapshot())

    # ===================== actions =====================

    def _txn(self, typ):
        self._push()
        try:
            amount = Decimal(self.v_amount.get().strip().replace(",", "."))
        except InvalidOperation:
            amount = Decimal(0)
        cur = re.search(r"\d{3}", self.v_cur.get())
        self.engine.start_txn(E.TxnRequest(
            type=typ, amount=amount, currency=cur.group(0) if cur else "840", decimals=self._int(self.v_dec, 2),
            ref=self.v_ref.get(), orig_ref=self.v_orig.get(), txn_id=self.v_txnid.get(), seq_no=self.v_seq.get(),
            repeat=self._int(self.v_rep, 1), repeat_interval_sec=self._int(self.v_repsec, 10),
        ))

    def _send_free(self):
        text = self.tx_free.get("1.0", "end-1c")
        try:
            data = build_free_text(text, self.cb_framing.current(), self.v_escapes.get(), self.v_hexin.get(), self.v_crlf.get())
        except ValueError as e:
            self._set_result("FREE TEXT NOT SENT", str(e), "bad")
            return
        if not data:
            self._set_result("FREE TEXT NOT SENT", "Type something to send.", "bad")
            return
        self._push()
        self.engine.send_raw(data, self._int(self.v_frep, 1), self._int(self.v_fint, 1000))
        entry = FRAMINGS[max(0, self.cb_framing.current())] + " | " + text.replace("\n", " ")
        if entry in self.history:
            self.history.remove(entry)
        self.history.insert(0, entry)
        del self.history[25:]
        self._refresh_history()

    def _refresh_history(self):
        self.cb_hist["values"] = ["History (%d)" % len(self.history)] + self.history
        self.cb_hist.current(0)

    def _history_picked(self):
        i = self.cb_hist.current()
        if i > 0:
            item = self.history[i - 1]
            framing, _, text = item.partition(" | ")
            if framing in FRAMINGS:
                self.cb_framing.current(FRAMINGS.index(framing))
            self.tx_free.delete("1.0", "end")
            self.tx_free.insert("1.0", text)
        self.cb_hist.current(0)

    def _copy_log(self):
        self.root.clipboard_clear()
        self.root.clipboard_append(self.tx_log.get("1.0", "end-1c"))

    def _open_logs(self):
        d = os.path.join(DATA, "logs")
        os.makedirs(d, exist_ok=True)
        subprocess.Popen(["open" if sys.platform == "darwin" else "xdg-open", d])

    def _about(self):
        messagebox.showinfo("About ECR Simulator", (
            "ECR Simulator %s (macOS)\n\n"
            "Plays the cash register (ECR) against the Areeba POS:\n"
            "- QS, HDMC, KV and KV Jordan (progressive + delivery ack)\n"
            "- Nexgo D20 Bluetooth base or a direct serial cable\n"
            "- always-open or open-per-transaction (duty free / Mega Tech)\n"
            "- Sale, Preauth, Completion, Refund, Void, Settlement, connection check\n"
            "- free text, repeats, fault tests, full byte log\n\n"
            "Created by %s.") % (VERSION, AUTHOR), parent=self.root)

    def _on_close(self):
        self._save()
        self.engine.shutdown()
        self.root.destroy()

    # ===================== refresh =====================

    def _set_result(self, title, body, kind):
        self.lb_res.configure(text=title, fg=OK if kind == "ok" else BAD if kind == "bad" else "black")
        self.tx_res.configure(state="normal")
        self.tx_res.delete("1.0", "end")
        self.tx_res.insert("1.0", body)
        self.tx_res.configure(state="disabled")

    def _update_buttons(self, s):
        fmt = self._fmt()
        hdmc, kv = fmt == "HDMC", fmt in ("KV", "KVJordan")
        can = not s["busy"] and s["repeat_left"] == 0

        def en(w, on):
            w.state(["!disabled"] if on else ["disabled"])
        for b in (self.bt_sale, self.bt_settle, self.bt_check):
            en(b, can)
        for b in (self.bt_preauth, self.bt_completion, self.bt_refund, self.bt_void):
            en(b, can and not hdmc)
        en(self.bt_stop, s["busy"] or s["repeat_left"] > 0)
        per_txn = self.v_pertxn.get()
        en(self.bt_connect, not per_txn and not s["connected"])
        en(self.bt_disconnect, s["connected"])
        en(self.en_txnid, kv)
        en(self.en_seq, kv)
        en(self.ck_kvack, fmt == "KVJordan")
        en(self.sp_ack, fmt == "KVJordan")
        en(self.sp_close, per_txn)
        en(self.sp_auto, not per_txn)
        self.bt_check.configure(text="Keep-alive" if kv else "IsConnected?")
        self.lb_status.configure(text="OPEN" if s["connected"] else "CLOSED", bg=OK if s["connected"] else "gray")

    def _poll(self):
        logs = self.engine.take_logs()
        if logs:
            for t, d, text in logs:
                self.tx_log.insert("end", "%s  %-7s  %s\n" % (t, d, E.show(text)), LOG_TAGS.get(d, "info"))
            lines = int(self.tx_log.index("end-1c").split(".")[0])
            if lines > 6000:
                self.tx_log.delete("1.0", "%d.0" % (lines - 5000))
            if self.v_follow.get():
                self.tx_log.see("end")
        s = self.engine.snapshot()
        if s["version"] != self.shown_version:
            self.shown_version = s["version"]
            self.lb_disp1.configure(text=s["display1"])
            self.lb_disp2.configure(text=s["display2"])
            self._set_result(s["result_title"], s["result_body"], s["result_kind"])
            last = s["last_approved"] or "No approved sale yet."
            if s["last_preauth_ref"]:
                last += "\nlast approved preauth: ref " + s["last_preauth_ref"]
            self.lb_last.configure(text=last)
            if s["last_sale_ref"] and s["last_sale_ref"] != self.seen_sale:
                self.seen_sale = s["last_sale_ref"]
                self.v_orig.set(s["last_sale_ref"])
                if s["last_sale_txn_id"]:
                    self.v_txnid.set(s["last_sale_txn_id"])
            if s["last_preauth_ref"] and s["last_preauth_ref"] != self.seen_preauth:
                self.seen_preauth = s["last_preauth_ref"]
                self.v_orig.set(s["last_preauth_ref"])
            if s["last_sent_ref"] and s["last_sent_ref"] != self.seen_sent:
                self.seen_sent = s["last_sent_ref"]
                if self.v_autoref.get():
                    self.v_ref.set(E.next_ref(s["last_sent_ref"]))
        self.lb_stats.configure(text=s["stats"] + ("   |   repeats left %d" % s["repeat_left"] if s["repeat_left"] else ""))
        self._update_buttons(s)
        self.root.after(100, self._poll)


def main():
    port = None
    if "--port" in sys.argv[1:-1]:
        port = sys.argv[sys.argv.index("--port") + 1]
    root = tk.Tk()
    App(root, port)
    root.mainloop()


if __name__ == "__main__":
    main()
