"""ECR Simulator engine: the cash register side of the Areeba POS ECR link.

Created by Ahmad El Lahib. Python 3.9+, standard library only. Same behaviour as the Windows
version (src/EcrEngine.cs). The serial port is passed in, so the logic runs anywhere: the tests
use a fake port, the Mac app uses mac_port.PosixSerial.

Formats: QS, HDMC, KV (direct) and KV Jordan (progressive messages + delivery ack).
"""

import datetime
import os
import re
import threading
import time
from collections import deque
from dataclasses import dataclass, replace
from decimal import ROUND_HALF_UP, Decimal

STX, ETX = "\x02", "\x03"
KV_START, KV_END = '"<START>"', '"<END>"'
FORMATS = ("QS", "HDMC", "KV", "KVJordan")
LINE_OFF, LINE_ON, LINE_DRIVER = 0, 1, 2
INF = float("inf")

CURRENCIES = {
    "840": "USD", "422": "LBP", "400": "JOD", "368": "IQD",
    "978": "EUR", "784": "AED", "682": "SAR", "818": "EGP",
}


class PortDead(Exception):
    """The handle no longer works: the device was unplugged, powered off or rebooted."""


class WriteBlocked(Exception):
    """The other side did not take the data before the write timeout."""


@dataclass
class Settings:
    port: str = ""
    baud: int = 115200
    data_bits: int = 8
    parity: str = "N"                 # N, O, E
    stop_bits: int = 1                # 1 or 2
    rts: int = LINE_OFF
    dtr: int = LINE_OFF
    fmt: str = "QS"                   # QS, HDMC, KV, KVJordan
    per_txn: bool = True              # duty free / Mega Tech: open per transaction, close after the result
    close_delay_ms: int = 0
    auto_reopen: bool = True
    handshake: bool = False           # IsConnected? (QS/HDMC) or keep-alive (KV) before each transaction
    ok_retries: int = 5
    ok_timeout_sec: int = 11
    result_timeout_sec: int = 120
    auto_check_sec: int = 0           # connection check every N s while idle and open (0 = off)
    kv_ack: bool = True               # KV Jordan: ack an approved sale, else the POS reverses it after 1.5 s
    kv_ack_delay_ms: int = 0
    log_hex: bool = False


@dataclass
class TxnRequest:
    type: str                         # Sale, Preauth, Completion, Refund, Void, Settlement
    amount: Decimal = Decimal(0)
    currency: str = "840"
    decimals: int = 2
    ref: str = ""
    orig_ref: str = ""
    txn_id: str = ""
    seq_no: str = ""
    repeat: int = 1
    repeat_interval_sec: int = 10


# ===================== helpers =====================

_CTRL = {STX: "<STX>", ETX: "<ETX>", "\r": "<CR>", "\n": "<LF>", "\x06": "<ACK>", "\x15": "<NAK>", "\x04": "<EOT>"}


def show(s):
    """Control characters as <STX>, <ETX>, <CR>, ... so a log line stays readable."""
    out = []
    for c in s:
        if c in _CTRL:
            out.append(_CTRL[c])
        elif c < " " or "\x7f" <= c < "\xa0":
            out.append("<%02X>" % ord(c))
        else:
            out.append(c)
    return "".join(out)


def to_hex(b):
    return " ".join("%02X" % x for x in b)


def lrc(body):
    x = 0
    for c in body:
        x ^= ord(c)
    return chr(x)


def qs_frame(text):
    """Register -> POS QS/HDMC frame: STX text ETX LRC (LRC = XOR of the text and ETX)."""
    body = text + ETX
    return STX + body + lrc(body)


def kv_frame(fields):
    return ",".join([KV_START] + ['"%s"' % f for f in fields] + [KV_END])


def digits(s):
    return "".join(c for c in (s or "") if c.isdigit())


def ref8(s):
    d = digits(s) or datetime.datetime.now().strftime("%H%M%S")
    return d[-8:].rjust(8, "0")


def next_ref(r):
    v = int(digits(r) or "0")
    return "%08d" % ((v + 1) % 100000000)


def currency_name(code):
    return CURRENCIES.get(code, code)


def format_amount(a, dec):
    q = Decimal(1).scaleb(-dec) if dec > 0 else Decimal(1)
    return str(Decimal(a).quantize(q, rounding=ROUND_HALF_UP))


def format_name(fmt):
    return "KV Jordan (progressive)" if fmt == "KVJordan" else fmt


def fields(m, kv):
    """key=value pairs of a QS TxnResp (one per line) or a KV message (quoted, comma separated)."""
    d = {}
    parts = m.replace('"', "").split(",") if kv else re.split(r"[\r\n]+", m.strip(STX + ETX))
    for raw in parts:
        line = raw.strip()
        i = line.find("=")
        if i > 0:
            d[line[:i].strip()] = line[i + 1:].strip()
    return d


def _get(f, k):
    v = f.get(k, "")
    return v if v else "-"


def _plain(m):
    for c in (STX, ETX, "\r", "\n"):
        m = m.replace(c, " ")
    return m.strip()


def _field_list(f):
    return "".join("%s = %s\n" % kv for kv in f.items())


class _Pending:
    def __init__(self, req=None, frame=None, title="", check_only=False):
        self.req, self.frame, self.title, self.check_only = req, frame, title, check_only


# ===================== engine =====================

class Engine:
    """All state lives on one worker thread. The window only queues commands and reads snapshots.

    open_port(settings) returns an object with write(bytes), read() -> bytes, set_rts(bool),
    set_dtr(bool), close() and a `before` text; port_exists(name) says if the port is present.
    """

    def __init__(self, open_port, port_exists, log_dir, now=time.monotonic):
        self._open_port = open_port
        self._port_exists = port_exists
        self.now = now
        self.lock = threading.RLock()
        self.commands = deque()
        self.new_logs = []
        self.cfg = Settings()
        self.sp = None
        self.rx = ""
        self.last_rx = 0.0
        self.state = "idle"              # idle | handshake | result
        self.pending = None
        self.last_sale = self.last_preauth = None
        self.last_sale_txn_id = self.last_sent_ref = None
        self.t0 = 0.0
        self.tries = 0
        self.repeat_left = 0
        self.repeat_req = None
        self.close_at = self.next_repeat_at = self.reopen_at = self.ack_at = self.next_auto_check = INF
        self.pause_until = None
        self.free_sends = deque()        # (at, text, index, count)
        self.ok_missing = self.write_blocked = self.port_lost = self.base_reboots = 0
        self.approved = self.declined = self.no_result = 0
        self.ok_times = []
        self.disp1, self.disp2 = "READY", ""
        self.res_title = self.res_body = self.res_kind = ""
        self.version = 0
        self.running = False
        self.thread = None
        self.log_file = None
        self._log_fh = None
        if log_dir:
            os.makedirs(log_dir, exist_ok=True)
            self.log_file = os.path.join(log_dir, "ecr-%s.log" % datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
            try:
                self._log_fh = open(self.log_file, "a", encoding="utf-8", buffering=1)
            except OSError:
                self._log_fh = None

    # ---------- public API (any thread) ----------

    def start(self):
        self.running = True
        self.thread = threading.Thread(target=self._loop, name="EcrEngine", daemon=True)
        self.thread.start()

    def shutdown(self):
        self.running = False
        if self.thread:
            self.thread.join(2)
        with self.lock:
            self._close_port("simulator closed")
            if self._log_fh:
                self._log_fh.close()
                self._log_fh = None

    def apply(self, s):
        c = replace(s)
        self._enqueue(lambda: self._apply_settings(c))

    def connect(self):
        def a():
            self.close_at = INF
            if self.sp is None:
                self._open()
        self._enqueue(a)

    def disconnect(self):
        def a():
            self.repeat_left = 0
            self.next_repeat_at = INF
            self._close_port("by the user")
            self._display("DISCONNECTED", "")
        self._enqueue(a)

    def check(self):
        self._enqueue(self._start_check)

    def start_txn(self, r):
        c = replace(r)

        def a():
            self.repeat_req = c
            self.repeat_left = max(0, c.repeat - 1)
            self._start_txn(replace(c))
        self._enqueue(a)

    def stop(self):
        def a():
            self.repeat_left = 0
            self.next_repeat_at = INF
            if self.state == "idle":
                return
            self._log("--", "stopped waiting (by the user)")
            self._give_up("STOPPED", "Stopped waiting. The terminal may still finish the transaction; its messages stay in the log.")
        self._enqueue(a)

    def send_raw(self, text, repeat=1, interval_ms=1000):
        def a():
            if not self._ensure_open():
                return
            n = max(1, repeat)
            t = self.now()
            for i in range(n):
                self.free_sends.append((t + i * max(0, interval_ms) / 1000.0, text, i, n))
        self._enqueue(a)

    def pause_reading(self, sec):
        def a():
            self.pause_until = self.now() + sec
            self._log("--", "TEST: not reading the port for %d s (frozen register)" % sec)
        self._enqueue(a)

    def close_port_for(self, sec):
        def a():
            if self.sp is None:
                self._log("!!", "TEST: the port is not open")
                return
            self._close_port("TEST: closed for %d s" % sec)
            self.reopen_at = self.now() + sec
        self._enqueue(a)

    def reset_stats(self):
        def a():
            self.ok_times = []
            self.ok_missing = self.write_blocked = self.port_lost = self.base_reboots = 0
            self.approved = self.declined = self.no_result = 0
            self._changed()
        self._enqueue(a)

    def take_logs(self):
        with self.lock:
            logs, self.new_logs = self.new_logs, []
            return logs

    def snapshot(self):
        with self.lock:
            ls = self.last_sale
            return {
                "connected": self.sp is not None,
                "busy": self.state != "idle",
                "display1": self.disp1,
                "display2": self.disp2,
                "result_title": self.res_title,
                "result_body": self.res_body,
                "result_kind": self.res_kind,
                "stats": self._stats_text(),
                "last_sale_ref": ls.ref if ls else None,
                "last_sale_txn_id": self.last_sale_txn_id,
                "last_preauth_ref": self.last_preauth.ref if self.last_preauth else None,
                "last_sent_ref": self.last_sent_ref,
                "last_approved": None if ls is None else "last approved sale: ref %s  %s %s%s" % (
                    ls.ref, format_amount(ls.amount, ls.decimals), currency_name(ls.currency),
                    "  TXNID " + self.last_sale_txn_id if self.last_sale_txn_id else ""),
                "repeat_left": self.repeat_left,
                "version": self.version,
            }

    # ---------- worker ----------

    def _enqueue(self, action):
        with self.lock:
            self.commands.append(action)

    def step(self):
        """Runs one queued command, or one tick when none is queued. Returns True when a command ran."""
        with self.lock:
            action = self.commands.popleft() if self.commands else None
            try:
                if action:
                    action()
                else:
                    self._tick()
            except Exception as e:  # keep the register alive whatever happens
                self._log("!!", "simulator error: %s" % e)
            return action is not None

    def _loop(self):
        while self.running:
            if not self.step():
                time.sleep(0.03)

    def _tick(self):
        now = self.now()
        if self.sp is not None and (self.pause_until is None or now >= self.pause_until):
            if self.pause_until is not None:
                self.pause_until = None
                self._log("--", "TEST: reading the port again")
            for m in self._read_messages():
                self._on_message(m)
        while self.free_sends and self.free_sends[0][0] <= now:
            _, text, i, n = self.free_sends.popleft()
            if self.sp is None or not self._write(text, "free text" + (" %d/%d" % (i + 1, n) if n > 1 else "")):
                self.free_sends.clear()
                break
            if not self.free_sends and self.state == "idle":
                self._schedule_close(5000)   # keep the port open a moment so the answer can be read
        if now >= self.ack_at:
            self.ack_at = INF
            self._send_kv_ack()
        if self.state == "handshake" and now - self.t0 >= self.cfg.ok_timeout_sec:
            self.ok_missing += 1
            self._log("!!", "no answer to the connection check within %d s" % self.cfg.ok_timeout_sec)
            if self.tries < self.cfg.ok_retries:
                self._ask_connected()
            else:
                self._give_up("NO ANSWER", "The terminal did not answer the connection check %d times.\n"
                              "Is the ECR screen open on the POS and the format the same (%s)?"
                              % (self.tries, format_name(self.cfg.fmt)))
        if self.state == "result" and now - self.t0 >= self.cfg.result_timeout_sec:
            self.no_result += 1
            self._log("!!", "NO result within %d s" % self.cfg.result_timeout_sec)
            self._give_up("NO RESULT", "No final answer from the terminal within %d s." % self.cfg.result_timeout_sec)
        if self.state == "idle" and now >= self.close_at and self.ack_at == INF and not self.free_sends:
            self.close_at = INF
            if self.cfg.per_txn and self.sp is not None:
                self._close_port("transaction finished (per-transaction mode)")
        if now >= self.reopen_at:
            self.reopen_at = INF
            self._log("--", "TEST: reopening the port")
            self._open()
        if self.state == "idle" and now >= self.next_repeat_at:
            self.next_repeat_at = INF
            if self.repeat_req is not None and self.repeat_left > 0:
                self.repeat_left -= 1
                r = replace(self.repeat_req, ref=next_ref(self.repeat_req.ref))
                self.repeat_req.ref = r.ref
                self._start_txn(r)
        if self.cfg.auto_check_sec > 0 and self.state == "idle" and self.sp is not None and not self.cfg.per_txn:
            if self.next_auto_check == INF:
                self.next_auto_check = now + self.cfg.auto_check_sec
            elif now >= self.next_auto_check:
                self.next_auto_check = INF
                self._start_check()
        else:
            self.next_auto_check = INF

    # ---------- log / state ----------

    def _changed(self):
        self.version += 1

    def _log(self, direction, text):
        t = datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]
        self.new_logs.append((t, direction, text))
        if len(self.new_logs) > 20000:
            del self.new_logs[:5000]
        if self._log_fh:
            try:
                self._log_fh.write("%s  %-7s  %s\n" % (t, direction, show(text)))
            except OSError:
                pass

    def _display(self, l1, l2):
        if l1 is not None:
            self.disp1 = l1
        if l2 is not None:
            self.disp2 = l2
        self._changed()

    def _result(self, title, body, kind):
        self.res_title, self.res_body, self.res_kind = title, body, kind
        self._changed()

    def _stats_text(self):
        s = "Approved %d   declined/cancelled %d   no result %d" % (self.approved, self.declined, self.no_result)
        if self.ok_times:
            t = sorted(self.ok_times)
            s += "   |   checks ok %d  median %.0f ms  max %.0f ms" % (len(t), t[len(t) // 2], t[-1])
        if self.ok_missing:
            s += "   missing %d" % self.ok_missing
        if self.write_blocked:
            s += "   PC writes blocked %d" % self.write_blocked
        if self.port_lost:
            s += "   dead port %d" % self.port_lost
        if self.base_reboots:
            s += "   D20 reboots %d" % self.base_reboots
        return s

    def _lines_text(self):
        def lt(m):
            return "1" if m == LINE_ON else "0" if m == LINE_OFF else "driver"
        warn = "  (a D20 base REBOOTS with RTS=1 DTR=0)" if self.cfg.rts == LINE_ON and self.cfg.dtr != LINE_ON else ""
        return "RTS=%s DTR=%s%s" % (lt(self.cfg.rts), lt(self.cfg.dtr), warn)

    def _port_text(self):
        c = self.cfg
        return "%s @ %d %d%s%d" % (c.port, c.baud, c.data_bits, c.parity, c.stop_bits)

    @property
    def _is_kv(self):
        return self.cfg.fmt in ("KV", "KVJordan")

    # ---------- settings / port ----------

    def _apply_settings(self, s):
        old, self.cfg = self.cfg, s
        if self.sp is not None:
            if s.rts != old.rts and s.rts != LINE_DRIVER:
                self._set_line("RTS", s.rts == LINE_ON)
            if s.dtr != old.dtr and s.dtr != LINE_DRIVER:
                self._set_line("DTR", s.dtr == LINE_ON)
            if (s.port, s.baud, s.data_bits, s.parity, s.stop_bits) != (old.port, old.baud, old.data_bits, old.parity, old.stop_bits):
                self._log("--", "port settings changed: they apply the next time the port opens (Disconnect, then Connect)")
        if s.per_txn != old.per_txn:
            if s.per_txn:
                if self.sp is not None and self.state == "idle":
                    self._close_port("per-transaction mode on")
                self._log("--", "mode: open the port per transaction, close it after the result (duty free / Mega Tech)")
                self._display("READY", "The port opens when you start a transaction")
            else:
                self.close_at = INF
                self._log("--", "mode: always open (press Connect)")
                self._display("NOT CONNECTED" if self.sp is None else "READY", "Press Connect" if self.sp is None else "")
        if s.fmt != old.fmt:
            self.rx = ""
            self._log("--", "format: " + format_name(s.fmt))

    def _set_line(self, name, on):
        try:
            if name == "RTS":
                self.sp.set_rts(on)
            else:
                self.sp.set_dtr(on)
            self._log("--", "%s set to %d on the open port -> %s" % (name, 1 if on else 0, self._lines_text()))
        except Exception as e:
            self._log("!!", "could not set %s: %s" % (name, e))

    def _open(self):
        if not self.cfg.port:
            self._log("!!", "no serial port selected")
            self._result("NO PORT", "Pick a serial port first.", "bad")
            return False
        try:
            self.sp = self._open_port(self.cfg)
            self.rx = ""
            self._log("--", "opened %s %s  (%s)" % (self._port_text(), self._lines_text(), getattr(self.sp, "before", "")))
            self._display("READY", "Port open")
            return True
        except Exception as e:
            self.sp = None
            present = self._port_exists(self.cfg.port)
            self._log("!!", "could not open %s: %s%s" % (self.cfg.port, e, "" if present else "  -> the port does not exist right now"))
            self._result("PORT NOT OPENED", "%s: %s\n%s" % (self.cfg.port, e,
                         "Only one program can use a serial port at a time. Close other simulators or serial tools."
                         if present else "The port does not exist right now: the D20 or the cable is unplugged, powered off or rebooting."), "bad")
            self._display("PORT NOT OPENED", "port busy" if present else "port not found")
            return False

    def _ensure_open(self):
        self.close_at = INF
        if self.sp is not None:
            return True
        if not self.cfg.per_txn:
            self._result("NOT CONNECTED", "Press Connect first, or choose \"Open per transaction\".", "bad")
            self._display("NOT CONNECTED", "Press Connect")
            return False
        return self._open()

    def _schedule_close(self, extra_ms):
        if self.cfg.per_txn and self.sp is not None:
            self.close_at = self.now() + (max(0, self.cfg.close_delay_ms) + extra_ms) / 1000.0

    def _close_port(self, why):
        if self.sp is not None:
            try:
                self.sp.close()
            except Exception:
                pass  # dead handle
            self.sp = None
            self._log("--", "closed %s%s" % (self.cfg.port, " (%s)" % why if why else ""))
        self.pending = None
        self.state = "idle"
        self.close_at = self.ack_at = INF
        self.free_sends.clear()
        self._changed()

    def _write(self, data, note=None):
        b = data.encode("latin-1", "replace")
        t = self.now()
        try:
            self.sp.write(b)
            ms = int((self.now() - t) * 1000)
            self._log("PC->POS", data + ("   (%s)" % note if note else "") + ("   (write took %d ms)" % ms if ms > 200 else ""))
            if self.cfg.log_hex:
                self._log("hex", to_hex(b))
            return True
        except PortDead as e:
            self.port_lost += 1
            self._log("!!", "PORT DEAD (stale port): %s" % e)
            if self.cfg.auto_reopen and self._reopen():
                try:
                    self.sp.write(b)
                    self._log("PC->POS", data)
                    return True
                except Exception as e2:
                    self._log("!!", "write failed again after reopening: %s" % e2)
            self._result("PORT DEAD (stale port)", "The device was unplugged, powered off or rebooted since %s was opened.\n"
                         "Connect again (or turn on \"Auto-reopen a dead port\")." % self.cfg.port, "bad")
            self._display("PORT DEAD", "Reconnect the serial port")
            self._close_port("dead handle")
            return False
        except Exception as e:
            ms = int((self.now() - t) * 1000)
            self.write_blocked += 1
            self._log("!!", "PC WRITE BLOCKED after %d ms (the other side did not take the data): %s" % (ms, e))
            return False

    def _reopen(self):
        try:
            if self.sp is not None:
                self.sp.close()
        except Exception:
            pass
        self.sp = None
        try:
            self.sp = self._open_port(self.cfg)
            self.rx = ""
            self._log("--", "reopened " + self._port_text())
            return True
        except Exception as e:
            self._log("!!", "could not reopen %s: %s" % (self.cfg.port, e))
            return False

    # ---------- reading ----------

    def _read_messages(self):
        out = []
        try:
            while self.sp is not None:
                chunk = self.sp.read()
                if not chunk:
                    break
                if self.cfg.log_hex:
                    self._log("hex", "read %d B: %s" % (len(chunk), to_hex(chunk)))
                self.rx += chunk.decode("latin-1")
                self.last_rx = self.now()
        except Exception as e:
            self.port_lost += 1
            self._log("!!", "PORT LOST while reading: %s" % e)
            self._result("PORT LOST", "The serial port closed (the device rebooted or the cable came out). Connect again.", "bad")
            self._display("PORT LOST", "")
            self._close_port("port lost")
            return out
        if "ESP-ROM" in self.rx:
            m = re.search(r"rst:(0x[0-9a-fA-F]+ \([^)]*\))", self.rx)
            self._log("!!", "D20 BASE REBOOTED - reset reason: " + (m.group(1) if m else "not captured"))
            self.base_reboots += 1
            self.rx = ""
            return out
        if self._is_kv:
            self._split_kv(out)
        else:
            self._split_stx_etx(out)
        # Leftover bytes that never become a message (wrong format or baud rate): show them after 1.5 s.
        if self.rx and self.now() - self.last_rx > 1.5:
            self._log("POS->PC", self.rx + "   (incomplete: no %s - wrong format or baud rate?)" % ('"<END>"' if self._is_kv else "ETX"))
            self.rx = ""
        return out

    def _split_stx_etx(self, out):
        while ETX in self.rx:
            i = self.rx.index(ETX)
            msg = self.rx[:i + 1].lstrip("\r\n")
            # The POS ends each message with CR LF after ETX; drop it so the next message starts clean.
            self.rx = self.rx[i + 1:].lstrip("\r\n")
            self._log("POS->PC", msg)
            out.append(msg)

    def _split_kv(self, out):
        # KV Jordan sends a message progressively (growing prefixes 300 ms apart, then the whole message):
        # only the text from the last "<START>" before each "<END>" is the message.
        while KV_END in self.rx:
            e = self.rx.index(KV_END)
            end = e + len(KV_END)
            start = self.rx.rfind(KV_START, 0, e)
            if start < 0:
                start = 0
            if start > 0:
                self._log("POS->PC", self.rx[:start] + "   (progressive chunks, %d bytes)" % start)
            msg = self.rx[start:end]
            self.rx = self.rx[end:].lstrip("\r\n")
            self._log("POS->PC", msg)
            out.append(msg)

    # ---------- protocol ----------

    def _check_frame(self):
        return kv_frame(["TXNID=0"]) if self._is_kv else "IsConnected?"

    def _start_check(self):
        if self.state != "idle":
            self._log("!!", "busy: a transaction is in progress")
            return
        if not self._ensure_open():
            return
        self.pending = _Pending(title="CONNECTION CHECK", check_only=True)
        self.tries = 0
        self._result("", "", "")
        self._ask_connected()

    def _ask_connected(self):
        self.tries += 1
        self.t0 = self.now()
        self.state = "handshake"
        p = self.pending
        self._display(p.title if p and not p.check_only else "CHECKING",
                      "%s (try %d of %d)" % ("Keep-alive" if self._is_kv else "IsConnected?", self.tries, self.cfg.ok_retries))
        if not self._write(self._check_frame(), "KV keep-alive" if self._is_kv else None) and self.state != "idle":
            self._give_up("PC WRITE BLOCKED", "The connection check could not be written to the port.")

    def _start_txn(self, r):
        if self.state != "idle":
            self._log("!!", "busy: a transaction is in progress")
            return
        frame, title = self._build_frame(r)
        if frame is None:
            self.repeat_left = 0
            return
        if not self._ensure_open():
            self.repeat_left = 0
            return
        self.pending = _Pending(req=r, frame=frame, title=title)
        if r.type in ("Sale", "Preauth", "Refund"):
            self.last_sent_ref = r.ref
        self._result("", "", "")
        self._display(title, "")
        if self.cfg.handshake:
            self.tries = 0
            self._ask_connected()
        else:
            self._send_pending()

    def _send_pending(self):
        self.t0 = self.now()
        self.state = "result"
        self._display(self.pending.title, "Waiting for the terminal...")
        if not self._write(self.pending.frame) and self.state != "idle":
            self._give_up("PC WRITE BLOCKED", "The transaction could not be written to the port.")

    def _give_up(self, title, body):
        self._result(title, body, "bad")
        self._display(title, "")
        self._end_txn()

    def _end_txn(self):
        self.pending = None
        self.state = "idle"
        self._schedule_close(0)
        if self.repeat_left > 0 and self.repeat_req is not None:
            self.next_repeat_at = self.now() + max(1, self.repeat_req.repeat_interval_sec)
        self._changed()

    def _build_frame(self, r):
        """The register's request for the selected format, or (None, reason shown) when it cannot be built."""
        incremental = r.type == "IncrementalPreauth"
        title = "INCREMENTAL" if incremental else r.type.upper()
        money = r.type not in ("Settlement", "Void")
        if money and r.amount <= 0:
            self._result("ENTER AN AMOUNT", "Enter an amount like 12.50", "bad")
            return None, None
        if self.cfg.fmt == "HDMC" and r.type not in ("Sale", "Settlement"):
            self._result("NOT IN HDMC", "HDMC only has Sale, Settlement and IsConnected?.", "bad")
            return None, None
        if incremental and not self._is_kv:
            # The POS reads InitiateTxn=Sale|Preauth|Completion|Void|Refund only (QSRepo), so it would ignore it.
            self._result("NOT IN QS", "Incremental preauth exists only in KV (and wireless) on the POS. "
                         "Switch the format to KV or KV Jordan.", "bad")
            return None, None
        # Void, Completion and Incremental point at an earlier transaction by its ECR ref.
        by_original = r.type in ("Void", "Completion") or incremental
        has_orig = bool(digits(r.orig_ref))
        if by_original and not has_orig and not (self._is_kv and r.type == "Void"):
            self._result("ORIGINAL REF MISSING", "Enter the ECR ref of the original %s in \"Original ref\"."
                         % ("sale" if r.type == "Void" else "preauth"), "bad")
            return None, None
        if self._is_kv and r.type == "Void" and not digits(r.txn_id):
            self._result("TXNID MISSING", "KV voids find the sale by TXNID (the STAN in the sale's response). "
                         "Enter it in \"KV TXNID\".", "bad")
            return None, None
        rf = ref8(r.orig_ref if by_original and has_orig else r.ref)
        r.ref = rf
        amount_text = format_amount(r.amount, r.decimals)
        if money:
            title += "  %s %s" % (amount_text, currency_name(r.currency))
        if by_original:
            title += "  ref " + rf

        if not self._is_kv:
            if r.type == "Settlement":
                return qs_frame("InitiateTxn=Settlement"), title
            dec = 2 if self.cfg.fmt == "HDMC" else r.decimals   # the POS reads HDMC amounts with 2 decimals
            minor = int((Decimal(r.amount) * (Decimal(10) ** dec)).quantize(Decimal(1), rounding=ROUND_HALF_UP))
            if minor >= 10 ** 12:
                self._result("AMOUNT TOO BIG", "The amount does not fit in 12 digits.", "bad")
                return None, None
            if self.cfg.fmt == "HDMC":
                return qs_frame("InitiateTxn=Sale&%s%012d" % (rf, minor)), title
            return qs_frame("InitiateTxn=%s&%s%012d%s" % (r.type, rf, minor, r.currency)), title

        f = []
        if r.type == "Settlement":
            f.append("CMD=SETTLE")
        elif r.type == "Void":
            f.append("CMD=VOID")
            f.append("TXNID=" + digits(r.txn_id))
            if has_orig:
                f.append("ECRREF=" + rf)
        else:
            cmd = "INCREMENTAL_PREAUTH" if incremental else r.type.upper()
            f += ["CMD=" + cmd, "AMT=" + amount_text, "CURRENCY=" + r.currency, "ECRREF=" + rf]
        if r.seq_no and r.seq_no.strip():
            f.append("TXNCONSEQUENTIALNO=" + r.seq_no.strip())
        return kv_frame(f), title

    def _on_message(self, m):
        if self._is_kv:
            self._on_kv_message(m)
        else:
            self._on_qs_message(m)

    def _handshake_ok(self):
        ms = (self.now() - self.t0) * 1000
        self.ok_times.append(ms)
        self._log("--", "connection ok after %.0f ms" % ms)
        if self.pending is not None and not self.pending.check_only:
            self._send_pending()
            return
        self._result("TERMINAL CONNECTED  (%.0f ms)" % ms,
                     "The POS answered the keep-alive with an empty frame." if self._is_kv else "The POS answered IsConnected? with ok.", "ok")
        self._display("READY", "connected, answer in %.0f ms" % ms)
        self._end_txn()

    def _on_qs_message(self, m):
        t = m.strip(STX + ETX + "\r\n ")
        if t.lower() == "ok":
            if self.state == "handshake":
                self._handshake_ok()
            return
        if t == "Idle-State":
            return
        final = t.startswith("TxnResp") or t.startswith("SettlementResp") or t in ("Cancelled", "Connect Failed", "Batch Empty")
        if not final:
            self._display(None, t)
            return
        if self.state != "result" or self.pending is None:
            self._log("--", "(final message outside a transaction)")
            return
        f = fields(m, False)
        got = f.get("EcrRefNo", "").strip()
        # A result left in the base from an earlier transaction comes out when the port opens again:
        # only accept the result that carries our ECR ref.
        if self.cfg.fmt == "QS" and t.startswith("TxnResp") and got and got != self.pending.req.ref:
            self._log("!!", "STALE result for ECR ref %s (waiting for %s) - ignored: %s %s %s RespCode %s RRN %s" % (
                got, self.pending.req.ref, _get(f, "Txn"), _get(f, "Amount"), _get(f, "Currency"), _get(f, "RespCode"), _get(f, "RRN")))
            self._display(None, "STALE result for ref %s ignored" % got)
            return
        secs = self.now() - self.t0
        self._log("==", "RESULT after %.1f s: %s" % (secs, re.split(r"[\r\n]", t)[0]))
        if t.startswith("SettlementResp"):
            self.approved += 1
            self._result("SETTLEMENT DONE  (%.1f s)" % secs, _field_list(f), "ok")
            self._display("SETTLEMENT DONE", "")
        elif t.startswith("TxnResp"):
            ok = _get(f, "RespCode") == "00"
            if ok:
                self.approved += 1
            else:
                self.declined += 1
            body = ("Txn %s   Resp %s %s\n" % (_get(f, "Txn"), _get(f, "RespCode"), _get(f, "RespMsg")) +
                    "Card %s   Entry %s   %s\n" % (_get(f, "Card"), _get(f, "Entry"), _get(f, "Card Issuer")) +
                    "RRN %s   Auth %s\n" % (_get(f, "RRN"), _get(f, "Auth")) +
                    "Invoice %s   Batch %s\n" % (_get(f, "Invoice"), _get(f, "Batch")) +
                    "ECR ref %s   TID %s   MID %s\n" % (_get(f, "EcrRefNo"), _get(f, "TID"), _get(f, "MID")) +
                    "Result in %.1f s\n\nAll fields:\n%s" % (secs, _field_list(f)))
            self._result(("APPROVED  " if ok else "DECLINED  ") + _get(f, "Amount") + " " + _get(f, "Currency"), body, "ok" if ok else "bad")
            self._display("APPROVED" if ok else "DECLINED", _get(f, "RespMsg"))
            if ok:
                self._remember(self.pending.req, _get(f, "Invoice"))
        else:
            self.declined += 1
            self._result(t.upper(), "The terminal ended the transaction with \"%s\" after %.1f s." % (t, secs), "bad")
            self._display(t.upper(), "")
        self._end_txn()

    def _on_kv_message(self, m):
        f = fields(m, True)
        f.pop("<START>", None)
        f.pop("<END>", None)
        if not re.sub(r'[\s",]|<START>|<END>', "", m):
            if self.state == "handshake":
                self._handshake_ok()
            return
        if "MirrorMsg" in f:
            self._display(None, f["MirrorMsg"])
            return
        if "STATUS" not in f:
            self._display(None, _plain(m))
            return
        if self.state != "result" or self.pending is None:
            self._log("--", "(final message outside a transaction)")
            return
        secs = self.now() - self.t0
        code = _get(f, "RESPONSECODE")
        ok = code == "00"
        if ok:
            self.approved += 1
        else:
            self.declined += 1
        typ = _get(f, "TXNTYPE")
        self._log("==", "RESULT after %.1f s: STATUS=%s RESPONSECODE=%s TXNTYPE=%s" % (secs, _get(f, "STATUS"), code, typ))
        body = ("Type %s   Status %s   Resp %s %s\n" % (typ, _get(f, "STATUS"), code, _get(f, "RSPTEXT")) +
                "PAN %s   Entry %s   %s\n" % (_get(f, "PAN"), _get(f, "ENTRYMODE"), _get(f, "CARDTYPE")) +
                "TXNID %s   RRN %s   Auth %s\n" % (_get(f, "TXNID"), _get(f, "REFERENCENO"), _get(f, "APPCODE")) +
                "Batch %s   TID %s   MID %s\n" % (_get(f, "BATCHNO"), _get(f, "TERMINALID"), _get(f, "MERCHANTID")) +
                "Result in %.1f s\n\nAll fields:\n%s" % (secs, _field_list(f)))
        if typ == "SETTLE":
            title = "SETTLEMENT DONE" if ok else "SETTLEMENT FAILED"
        else:
            title = ("APPROVED  " if ok else "CANCELLED  " if code == "UC" else "DECLINED  ") + _get(f, "AMT")
        self._result(title, body, "ok" if ok else "bad")
        self._display("APPROVED" if ok else "DECLINED", _get(f, "RSPTEXT"))
        if ok and typ != "SETTLE":
            self._remember(self.pending.req, _get(f, "TXNID"))
        # KV Jordan: the POS reverses an approved sale unless the register acks it with an empty frame within 1.5 s.
        if self.cfg.fmt == "KVJordan" and ok and typ == "SALE":
            if self.cfg.kv_ack:
                self.ack_at = self.now() + max(0, self.cfg.kv_ack_delay_ms) / 1000.0
            else:
                self._log("!!", "delivery ack OFF: the POS should reverse this sale after 1.5 s")
        self._end_txn()

    def _send_kv_ack(self):
        if self.sp is None:
            self._log("!!", "delivery ack not sent: the port is closed")
            return
        delay = self.cfg.kv_ack_delay_ms
        self._write(kv_frame([]), "delivery ack" + (" after %d ms" % delay if delay > 0 else ""))
        self._schedule_close(0)

    def _remember(self, r, txn_id):
        if r.type == "Sale":
            self.last_sale = r
            self.last_sale_txn_id = None if txn_id == "-" else txn_id
            self._log("--", "approved sale saved for Void: ref %s%s" % (
                r.ref, ", TXNID/Invoice " + self.last_sale_txn_id if self.last_sale_txn_id else ""))
        elif r.type == "Preauth":
            self.last_preauth = r
            self._log("--", "approved preauth saved for Completion: ref " + r.ref)
        self._changed()
