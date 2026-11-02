// ECR Simulator - window. Created by Ahmad El Lahib.
// C# 5 only (compiled in memory by Windows PowerShell 5.1, or by the .NET 4 csc in build.cmd).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace EcrSim
{
    public static class Program
    {
        public const string Version = "2.2";
        public const string Author = "Ahmad El Lahib";

        [STAThread]
        public static void Main(string[] args) { Run(AppDomain.CurrentDomain.BaseDirectory, args); }

        // baseDir: where the settings file and the logs folder live. args: --port COMx, --snapshot file.png
        public static void Run(string baseDir, string[] args)
        {
            try { Application.EnableVisualStyles(); } catch (Exception) { /* already set by the host */ }
            string snapshot = null, port = null;
            for (int i = 0; args != null && i < args.Length - 1; i++)
            {
                if (args[i] == "--snapshot") snapshot = args[i + 1];
                if (args[i] == "--port") port = args[i + 1];
            }
            try
            {
                using (var f = new MainForm(baseDir, port, snapshot)) Application.Run(f);
            }
            catch (Exception e)
            {
                MessageBox.Show("The ECR Simulator could not start:\n\n" + e.Message, "ECR Simulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // The icon embedded in the .exe (build.cmd), else assets\ecr.ico next to the launcher.
        public static Icon AppIcon(string baseDir)
        {
            try
            {
                var s = typeof(Program).Assembly.GetManifestResourceStream("EcrSim.ecr.ico");
                if (s != null) using (s) return new Icon(s);
                string f = Path.Combine(Path.Combine(baseDir, "assets"), "ecr.ico");
                if (File.Exists(f)) return new Icon(f);
            }
            catch (Exception) { /* no icon: Windows shows the default one */ }
            return null;
        }
    }

    public sealed class MainForm : Form
    {
        static readonly Color Accent = Color.FromArgb(0, 92, 175);
        static readonly Color OkColor = Color.FromArgb(22, 128, 60);
        static readonly Color BadColor = Color.FromArgb(190, 30, 30);
        static readonly Font Bold = new Font("Segoe UI", 9f, FontStyle.Bold);

        static readonly string[] Cables =
        {
            "Nexgo D20 Bluetooth base (USB)  -  115200, RTS 0, DTR 0",
            "Direct cable to the terminal  -  9600, RTS 1, DTR 1",
            "Custom",
        };
        static readonly string[] Formats = { "QS (query string)", "HDMC", "KV (direct)", "KV Jordan (progressive + delivery ack)" };
        static readonly string[] Currencies = { "840 USD", "422 LBP", "400 JOD", "368 IQD", "978 EUR", "784 AED", "682 SAR", "818 EGP" };
        static readonly string[] Framings =
        {
            "As typed",
            "QS register frame: STX text ETX LRC",
            "POS style: STX text ETX CR LF",
            "KV frame: \"<START>\",\"field\",...,\"<END>\"",
        };

        readonly string baseDir, iniPath, snapshotPath;
        readonly EcrEngine engine;
        readonly Dictionary<string, string> ini = new Dictionary<string, string>();
        readonly List<string> history = new List<string>();
        bool loading = true;
        long shownVersion = -1;
        string seenSaleRef, seenPreauthRef, seenSentRef;

        // connection
        ComboBox cbPort, cbCable, cbBaud, cbData, cbParity, cbStop, cbRts, cbDtr;
        RadioButton rbAlways, rbPerTxn;
        NumericUpDown nuCloseDelay;
        CheckBox ckReopen;
        Button btConnect, btDisconnect;
        Label lbStatus;
        // protocol
        ComboBox cbFormat;
        CheckBox ckHandshake, ckKvAck;
        NumericUpDown nuTries, nuOkSec, nuResultSec, nuAutoCheck, nuAckDelay;
        // transaction
        TextBox tbAmount, tbRef, tbOrigRef, tbTxnId, tbSeqNo;
        ComboBox cbCurrency;
        NumericUpDown nuDecimals, nuRepeat, nuRepeatSec;
        CheckBox ckAutoRef;
        Button btSale, btPreauth, btCompletion, btIncremental, btRefund, btVoid, btSettle, btCheck, btStop;
        Label lbLast;
        // tests
        NumericUpDown nuPause, nuClosed;
        // display / result
        Label lbDisp1, lbDisp2, lbResTitle, lbStats;
        TextBox tbResult;
        // free text
        TextBox tbFree;
        ComboBox cbFraming, cbHistory;
        CheckBox ckEscapes, ckHexIn, ckCrLf;
        NumericUpDown nuFreeRepeat, nuFreeInterval;
        // log
        RichTextBox rtLog;
        CheckBox ckLogHex, ckAutoScroll;

        public MainForm(string baseDir, string port, string snapshotPath)
        {
            this.baseDir = baseDir;
            this.snapshotPath = snapshotPath;
            iniPath = Path.Combine(baseDir, "ecr-simulator.ini");
            LoadIni();
            engine = new EcrEngine(Path.Combine(baseDir, "logs"));

            Text = "ECR Simulator " + Program.Version + "  -  by " + Program.Author;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(244, 246, 249);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1340, wa.Width), Math.Min(940, wa.Height));
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;
            var icon = Program.AppIcon(baseDir);
            if (icon != null) Icon = icon;

            BuildUi();
            RefreshPorts(port ?? Ini("port", ""));
            ApplyIni();
            loading = false;
            PushSettings();

            var timer = new Timer { Interval = 100 };
            timer.Tick += delegate { Poll(); };
            timer.Start();

            FormClosing += delegate { SaveIni(); engine.Dispose(); };
            if (snapshotPath != null)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-3000, -3000);
                Shown += delegate
                {
                    var t = new Timer { Interval = 800 };
                    t.Tick += delegate
                    {
                        t.Stop();
                        Poll();
                        using (var bmp = new Bitmap(Width, Height))
                        {
                            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                            bmp.Save(snapshotPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Close();
                    };
                    t.Start();
                };
            }
        }

        // ===================== layout =====================

        void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Accent };
            var title = new Label { Text = "ECR Simulator", ForeColor = Color.White, Font = new Font("Segoe UI", 16f, FontStyle.Bold), AutoSize = true, Location = new Point(14, 4) };
            var sub = new Label
            {
                Text = "Cash register for the Areeba POS ECR link  -  QS, HDMC, KV, KV Jordan  -  Bluetooth base or direct cable  -  by " + Program.Author,
                ForeColor = Color.FromArgb(220, 232, 245), AutoSize = true, Location = new Point(16, 33),
            };
            var about = new Button { Text = "About", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Size = new Size(70, 28), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            about.FlatAppearance.BorderColor = Color.White;
            about.Location = new Point(header.Width - 84, 13);
            about.Click += delegate { ShowAbout(); };
            header.Controls.AddRange(new Control[] { title, sub, about });
            header.Resize += delegate { about.Left = header.Width - 84; };

            var left = new Panel { Dock = DockStyle.Left, Width = 462, AutoScroll = true, Padding = new Padding(8, 6, 4, 8) };
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 6, 8, 8) };
            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(header);

            BuildConnection(left);
            BuildProtocol(left);
            BuildTransaction(left);
            BuildTests(left);
            BuildRight(right);
        }

        static GroupBox Group(Control parent, string text, int y, int h)
        {
            var g = new GroupBox { Text = text, Location = new Point(8, y), Size = new Size(432, h), Font = Bold, BackColor = Color.White };
            parent.Controls.Add(g);
            return g;
        }

        static Label L(Control p, string text, int x, int y)
        {
            var l = new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true, Font = new Font("Segoe UI", 9f) };
            p.Controls.Add(l);
            return l;
        }

        static ComboBox C(Control p, int x, int y, int w, bool editable, params string[] items)
        {
            var c = new ComboBox { Location = new Point(x, y), Width = w, DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 9f) };
            c.Items.AddRange(items);
            if (items.Length > 0) c.SelectedIndex = 0;
            p.Controls.Add(c);
            return c;
        }

        static TextBox T(Control p, int x, int y, int w, string text)
        {
            var t = new TextBox { Location = new Point(x, y), Width = w, Text = text, Font = new Font("Segoe UI", 9f) };
            p.Controls.Add(t);
            return t;
        }

        static NumericUpDown N(Control p, int x, int y, int w, int min, int max, int value)
        {
            var n = new NumericUpDown { Location = new Point(x, y), Width = w, Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Font = new Font("Segoe UI", 9f) };
            p.Controls.Add(n);
            return n;
        }

        static CheckBox K(Control p, string text, int x, int y, bool on)
        {
            var k = new CheckBox { Text = text, Location = new Point(x, y), AutoSize = true, Checked = on, Font = new Font("Segoe UI", 9f) };
            p.Controls.Add(k);
            return k;
        }

        static Button B(Control p, string text, int x, int y, int w, int h, EventHandler click)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = new Font("Segoe UI", 9f), BackColor = Color.FromArgb(236, 240, 245), UseVisualStyleBackColor = false };
            b.Click += click;
            p.Controls.Add(b);
            return b;
        }

        void BuildConnection(Control p)
        {
            var g = Group(p, "1. Connection (cable)", 6, 268);
            L(g, "Port", 10, 24);
            cbPort = C(g, 62, 24, 296, true);
            B(g, "Refresh", 364, 23, 60, 25, delegate { RefreshPorts(PortName()); });
            L(g, "Cable", 10, 54);
            cbCable = C(g, 62, 54, 362, false, Cables);
            L(g, "Baud", 10, 84);
            cbBaud = C(g, 62, 84, 78, true, "115200", "57600", "38400", "19200", "9600", "4800", "2400");
            L(g, "Data", 148, 84);
            cbData = C(g, 182, 84, 42, false, "8", "7");
            L(g, "Parity", 232, 84);
            cbParity = C(g, 276, 84, 64, false, "None", "Odd", "Even", "Mark", "Space");
            L(g, "Stop", 346, 84);
            cbStop = C(g, 380, 84, 44, false, "1", "1.5", "2");
            L(g, "RTS", 10, 114);
            cbRts = C(g, 62, 114, 78, false, "0 (off)", "1 (on)", "driver");
            L(g, "DTR", 148, 114);
            cbDtr = C(g, 182, 114, 78, false, "0 (off)", "1 (on)", "driver");
            L(g, "D20 base: keep RTS 0", 268, 114).ForeColor = Color.DimGray;

            rbAlways = new RadioButton { Text = "Always open  (Connect / Disconnect)", Location = new Point(10, 146), AutoSize = true, Font = Font };
            rbPerTxn = new RadioButton { Text = "Open per transaction  (duty free / Mega Tech: open, sale, result, close)", Location = new Point(10, 170), AutoSize = true, Font = Font };
            g.Controls.Add(rbAlways);
            g.Controls.Add(rbPerTxn);
            L(g, "Close the port", 28, 196);
            nuCloseDelay = N(g, 118, 195, 64, 0, 60000, 0);
            L(g, "ms after the result", 186, 196);
            ckReopen = K(g, "Auto-reconnect", 10, 224, true);
            new ToolTip().SetToolTip(ckReopen, "Cable pulled or port lost: reopen the port as soon as it is back and keep waiting for the result of the sale in progress.");

            btConnect = B(g, "Connect", 196, 220, 72, 30, delegate { PushSettings(); engine.Connect(); });
            btDisconnect = B(g, "Disconnect", 272, 220, 80, 30, delegate { engine.Disconnect(); });
            lbStatus = new Label { Location = new Point(358, 220), Size = new Size(70, 30), TextAlign = ContentAlignment.MiddleCenter, Font = Bold, ForeColor = Color.White };
            g.Controls.Add(lbStatus);

            cbCable.SelectedIndexChanged += delegate { if (!loading) ApplyCable(); };
            cbPort.SelectedIndexChanged += delegate
            {
                if (loading) return;
                if (EcrEngine.IsD20(cbPort.Text) && cbCable.SelectedIndex != 0) cbCable.SelectedIndex = 0;
                PushSettings();
            };
            foreach (var c in new Control[] { cbBaud, cbData, cbParity, cbStop, cbRts, cbDtr }) Watch(c, true);
            Watch(cbPort, false);
            Watch(rbPerTxn, false);
            Watch(nuCloseDelay, false);
            Watch(ckReopen, false);
        }

        void BuildProtocol(Control p)
        {
            var g = Group(p, "2. Protocol", 280, 174);
            L(g, "Format", 10, 24);
            cbFormat = C(g, 62, 24, 362, false, Formats);
            ckHandshake = K(g, "Connection check before each transaction (IsConnected? / KV keep-alive)", 10, 54, false);
            L(g, "Tries", 28, 80);
            nuTries = N(g, 66, 79, 46, 1, 99, 5);
            L(g, "wait", 118, 80);
            nuOkSec = N(g, 150, 79, 46, 1, 300, 11);
            L(g, "s each", 198, 80);
            L(g, "Result timeout", 258, 80);
            nuResultSec = N(g, 352, 79, 56, 5, 3600, 120);
            L(g, "Auto check every", 10, 110);
            nuAutoCheck = N(g, 118, 109, 56, 0, 3600, 0);
            L(g, "s while idle (always-open mode, 0 = off)", 178, 110);
            ckKvAck = K(g, "KV Jordan: send the delivery ack after an approved sale", 10, 140, true);
            L(g, "delay", 340, 141);
            nuAckDelay = N(g, 374, 140, 50, 0, 10000, 0);
            new ToolTip().SetToolTip(nuAckDelay, "ms. The POS reverses the sale when the ack takes more than 1.5 s - use 2000 to test the reversal.");
            cbFormat.SelectedIndexChanged += delegate { if (!loading) { UpdateButtons(); } };
            foreach (var c in new Control[] { cbFormat, ckHandshake, nuTries, nuOkSec, nuResultSec, nuAutoCheck, ckKvAck, nuAckDelay }) Watch(c, false);
        }

        void BuildTransaction(Control p)
        {
            var g = Group(p, "3. Transaction", 460, 336);
            L(g, "Amount", 10, 24);
            tbAmount = T(g, 70, 24, 90, "1.00");
            cbCurrency = C(g, 168, 24, 100, true, Currencies);
            L(g, "Decimals", 280, 24);
            nuDecimals = N(g, 340, 24, 44, 0, 4, 2);
            L(g, "ECR ref", 10, 54);
            tbRef = T(g, 70, 54, 90, DateTime.Now.ToString("ddHHmmss", CultureInfo.InvariantCulture));
            ckAutoRef = K(g, "+1 after each new sale / preauth / refund", 168, 56, true);
            L(g, "Original ref", 10, 84);
            tbOrigRef = T(g, 90, 84, 90, "");
            L(g, "KV TXNID", 196, 84);
            tbTxnId = T(g, 262, 84, 90, "");
            new ToolTip().SetToolTip(tbOrigRef, "ECR ref of the sale to void / the preauth to complete. Filled in by itself after an approval.");
            new ToolTip().SetToolTip(tbTxnId, "KV void: the TXNID (STAN) from the sale's response. Filled in by itself after an approval.");
            L(g, "KV txn seq no", 10, 114);
            tbSeqNo = T(g, 100, 114, 60, "");
            L(g, "Repeat", 196, 114);
            nuRepeat = N(g, 244, 113, 50, 1, 9999, 1);
            L(g, "x every", 298, 114);
            nuRepeatSec = N(g, 348, 113, 50, 1, 3600, 10);
            L(g, "s", 402, 114);

            int y = 148, w = 100, h = 36;
            btSale = B(g, "Sale", 10, y, w, h, delegate { Txn("Sale"); });
            btPreauth = B(g, "Preauth", 114, y, w, h, delegate { Txn("Preauth"); });
            btCompletion = B(g, "Completion", 218, y, w, h, delegate { Txn("Completion"); });
            btRefund = B(g, "Refund", 322, y, w, h, delegate { Txn("Refund"); });
            btVoid = B(g, "Void", 10, y + 40, w, h, delegate { Txn("Void"); });
            btSettle = B(g, "Settlement", 114, y + 40, w, h, delegate { Txn("Settlement"); });
            btCheck = B(g, "Check connection", 218, y + 40, w, h, delegate { PushSettings(); engine.Check(); });
            btStop = B(g, "Stop waiting", 322, y + 40, w, h, delegate { engine.Stop(); });
            btIncremental = B(g, "Incremental", 10, y + 80, w, h, delegate { Txn("IncrementalPreauth"); });
            new ToolTip().SetToolTip(btIncremental, "Incremental preauth (KV only): adds the amount to the preauth whose ECR ref is in \"Original ref\".");
            btSale.Font = btPreauth.Font = btRefund.Font = Bold;
            btSale.BackColor = Color.FromArgb(214, 232, 250);
            lbLast = new Label { Location = new Point(10, y + 124), Size = new Size(414, 56), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.5f) };
            g.Controls.Add(lbLast);
        }

        void BuildTests(Control p)
        {
            var g = Group(p, "4. Fault tests", 802, 92);
            L(g, "Stop reading the port for", 10, 26);
            nuPause = N(g, 168, 25, 56, 1, 3600, 60);
            L(g, "s", 226, 26);
            B(g, "Go", 244, 23, 46, 26, delegate { engine.PauseReading((int)nuPause.Value); });
            L(g, "Close the port for", 10, 58);
            nuClosed = N(g, 168, 57, 56, 1, 3600, 60);
            L(g, "s", 226, 58);
            B(g, "Go", 244, 55, 46, 26, delegate { engine.ClosePortFor((int)nuClosed.Value); });
            B(g, "Reset counters", 310, 23, 112, 26, delegate { engine.ResetStats(); });
        }

        void BuildRight(Control p)
        {
            // Bottom-up docking: the log fills what is left.
            var logBox = new GroupBox { Text = "Log  (PC->POS = sent by this register, POS->PC = from the terminal)", Dock = DockStyle.Fill, Font = Bold, BackColor = Color.White, Padding = new Padding(6) };
            var logBar = new Panel { Dock = DockStyle.Top, Height = 30 };
            ckAutoScroll = K(logBar, "Follow", 4, 6, true);
            ckLogHex = K(logBar, "Hex of every read / write", 70, 6, false);
            B(logBar, "Clear", 240, 2, 60, 26, delegate { rtLog.Clear(); });
            B(logBar, "Copy all", 304, 2, 70, 26, delegate { if (rtLog.TextLength > 0) Clipboard.SetText(rtLog.Text); });
            B(logBar, "Open logs folder", 378, 2, 110, 26, delegate { OpenFolder(Path.Combine(baseDir, "logs")); });
            var logFile = new Label { Text = Path.GetFileName(engine.LogFile), AutoSize = true, Location = new Point(496, 8), ForeColor = Color.DimGray, Font = Font };
            logBar.Controls.Add(logFile);
            rtLog = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(250, 251, 253), WordWrap = true, DetectUrls = false, BorderStyle = BorderStyle.None };
            logBox.Controls.Add(rtLog);
            logBox.Controls.Add(logBar);
            Watch(ckLogHex, false);

            var free = new GroupBox { Text = "Free text  (send anything on this cable)", Dock = DockStyle.Top, Height = 168, Font = Bold, BackColor = Color.White };
            tbFree = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(10, 22), Height = 54, Font = new Font("Consolas", 9.5f), Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, Text = "IsConnected?" };
            free.Controls.Add(tbFree);
            L(free, "Frame", 10, 84);
            cbFraming = C(free, 54, 84, 290, false, Framings);
            ckCrLf = K(free, "+ CR LF", 352, 86, false);
            ckEscapes = K(free, "Escapes  <STX> <ETX> <CR> <LF> <ACK> \\x1C", 10, 114, true);
            ckHexIn = K(free, "Text is hex bytes (02 41 03)", 290, 114, false);
            L(free, "Send", 10, 140);
            nuFreeRepeat = N(free, 46, 139, 50, 1, 1000, 1);
            L(free, "x every", 100, 140);
            nuFreeInterval = N(free, 150, 139, 64, 0, 600000, 1000);
            L(free, "ms", 218, 140);
            var send = B(free, "Send", 246, 136, 90, 28, delegate { SendFree(); });
            send.Font = Bold;
            send.BackColor = Color.FromArgb(214, 232, 250);
            cbHistory = C(free, 344, 139, 200, false);
            cbHistory.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            cbHistory.SelectedIndexChanged += delegate
            {
                if (cbHistory.SelectedIndex <= 0) return;
                string item = (string)cbHistory.SelectedItem;
                int bar = item.IndexOf(" | ", StringComparison.Ordinal);
                if (bar > 0) { cbFraming.SelectedIndex = Math.Max(0, Array.IndexOf(Framings, item.Substring(0, bar))); tbFree.Text = item.Substring(bar + 3); }
                cbHistory.SelectedIndex = 0;
            };
            free.Resize += delegate
            {
                tbFree.Width = free.Width - 20;
                cbHistory.Width = Math.Max(120, free.Width - 354);
            };

            var res = new GroupBox { Text = "Result", Dock = DockStyle.Top, Height = 222, Font = Bold, BackColor = Color.White, Padding = new Padding(8, 4, 8, 6) };
            lbResTitle = new Label { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 13f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            lbStats = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.5f) };
            tbResult = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = Color.White, BorderStyle = BorderStyle.None };
            res.Controls.Add(tbResult);
            res.Controls.Add(lbStats);
            res.Controls.Add(lbResTitle);

            var disp = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Color.FromArgb(24, 32, 40), Padding = new Padding(12, 6, 12, 6) };
            lbDisp2 = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 220, 255), Font = new Font("Consolas", 11f), TextAlign = ContentAlignment.TopLeft };
            lbDisp1 = new Label { Dock = DockStyle.Top, Height = 34, ForeColor = Color.FromArgb(120, 255, 160), Font = new Font("Consolas", 17f, FontStyle.Bold) };
            disp.Controls.Add(lbDisp2);
            disp.Controls.Add(lbDisp1);

            var gap1 = new Panel { Dock = DockStyle.Top, Height = 6 };
            var gap2 = new Panel { Dock = DockStyle.Top, Height = 6 };
            var gap3 = new Panel { Dock = DockStyle.Top, Height = 6 };
            p.Controls.Add(logBox);
            p.Controls.Add(gap3);
            p.Controls.Add(free);
            p.Controls.Add(gap2);
            p.Controls.Add(res);
            p.Controls.Add(gap1);
            p.Controls.Add(disp);
        }

        void Watch(Control c, bool cableField)
        {
            EventHandler h = delegate
            {
                if (loading) return;
                if (cableField && cbCable.SelectedIndex != 2 && !MatchesCable(cbCable.SelectedIndex))
                {
                    loading = true;
                    cbCable.SelectedIndex = 2;
                    loading = false;
                }
                PushSettings();
            };
            var cb = c as ComboBox;
            if (cb != null) { cb.SelectedIndexChanged += h; cb.TextChanged += h; return; }
            var ck = c as CheckBox;
            if (ck != null) { ck.CheckedChanged += h; return; }
            var rb = c as RadioButton;
            if (rb != null) { rb.CheckedChanged += h; return; }
            var nu = c as NumericUpDown;
            if (nu != null) { nu.ValueChanged += h; return; }
        }

        // ===================== settings =====================

        string PortName()
        {
            string t = cbPort.Text.Trim();
            int sp = t.IndexOf(' ');
            return sp > 0 ? t.Substring(0, sp) : t;
        }

        void RefreshPorts(string select)
        {
            bool was = loading;
            loading = true;
            cbPort.Items.Clear();
            int pick = -1;
            foreach (var kv in EcrEngine.Ports())
            {
                int i = cbPort.Items.Add(kv.Value);
                if (string.Equals(kv.Key, select, StringComparison.OrdinalIgnoreCase)) pick = i;
            }
            if (pick >= 0) cbPort.SelectedIndex = pick;
            else if (!string.IsNullOrEmpty(select)) cbPort.Text = select + "  (not present now)";
            else if (cbPort.Items.Count > 0) cbPort.SelectedIndex = 0;
            loading = was;
            if (!loading) PushSettings();
        }

        void ApplyCable()
        {
            loading = true;
            if (cbCable.SelectedIndex == 0) { cbBaud.Text = "115200"; cbRts.SelectedIndex = 0; cbDtr.SelectedIndex = 0; }
            else if (cbCable.SelectedIndex == 1) { cbBaud.Text = "9600"; cbRts.SelectedIndex = 1; cbDtr.SelectedIndex = 1; }
            if (cbCable.SelectedIndex != 2) { cbData.SelectedIndex = 0; cbParity.SelectedIndex = 0; cbStop.SelectedIndex = 0; }
            loading = false;
            PushSettings();
        }

        bool MatchesCable(int i)
        {
            bool common = cbData.SelectedIndex == 0 && cbParity.SelectedIndex == 0 && cbStop.SelectedIndex == 0;
            if (i == 0) return common && cbBaud.Text == "115200" && cbRts.SelectedIndex == 0 && cbDtr.SelectedIndex == 0;
            if (i == 1) return common && cbBaud.Text == "9600" && cbRts.SelectedIndex == 1 && cbDtr.SelectedIndex == 1;
            return true;
        }

        EcrFormat Format() { return (EcrFormat)Math.Max(0, cbFormat.SelectedIndex); }

        Settings ReadSettings()
        {
            int baud;
            if (!int.TryParse(cbBaud.Text.Trim(), out baud) || baud <= 0) baud = 9600;
            return new Settings
            {
                Port = PortName(),
                Baud = baud,
                DataBits = cbData.SelectedIndex == 1 ? 7 : 8,
                Parity = Math.Max(0, cbParity.SelectedIndex),
                StopBits = Math.Max(0, cbStop.SelectedIndex),
                Rts = (LineMode)Math.Max(0, cbRts.SelectedIndex),
                Dtr = (LineMode)Math.Max(0, cbDtr.SelectedIndex),
                Format = Format(),
                PerTxn = rbPerTxn.Checked,
                CloseDelayMs = (int)nuCloseDelay.Value,
                AutoReopen = ckReopen.Checked,
                Handshake = ckHandshake.Checked,
                OkRetries = (int)nuTries.Value,
                OkTimeoutSec = (int)nuOkSec.Value,
                ResultTimeoutSec = (int)nuResultSec.Value,
                AutoCheckSec = (int)nuAutoCheck.Value,
                KvAck = ckKvAck.Checked,
                KvAckDelayMs = (int)nuAckDelay.Value,
                LogHex = ckLogHex.Checked,
            };
        }

        void PushSettings()
        {
            if (loading) return;
            engine.Apply(ReadSettings());
            UpdateButtons();
        }

        void LoadIni()
        {
            try
            {
                if (!File.Exists(iniPath)) return;
                foreach (var line in File.ReadAllLines(iniPath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1);
                    if (k == "history") history.Add(v); else ini[k] = v;
                }
            }
            catch (Exception) { /* start with defaults */ }
        }

        string Ini(string k, string def) { string v; return ini.TryGetValue(k, out v) ? v : def; }
        int IniInt(string k, int def) { int v; return int.TryParse(Ini(k, ""), out v) ? v : def; }
        bool IniBool(string k, bool def) { string v = Ini(k, ""); return v == "" ? def : v == "1"; }

        static void SetNum(NumericUpDown n, int v) { n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }
        static void SetIdx(ComboBox c, int i) { if (i >= 0 && i < c.Items.Count) c.SelectedIndex = i; }

        void ApplyIni()
        {
            SetIdx(cbCable, IniInt("cable", 0));
            cbBaud.Text = Ini("baud", "115200");
            SetIdx(cbData, IniInt("data", 0));
            SetIdx(cbParity, IniInt("parity", 0));
            SetIdx(cbStop, IniInt("stop", 0));
            SetIdx(cbRts, IniInt("rts", 0));
            SetIdx(cbDtr, IniInt("dtr", 0));
            if (IniBool("perTxn", true)) rbPerTxn.Checked = true; else rbAlways.Checked = true;
            SetNum(nuCloseDelay, IniInt("closeDelay", 0));
            ckReopen.Checked = IniBool("autoReopen", true);
            SetIdx(cbFormat, IniInt("format", 0));
            ckHandshake.Checked = IniBool("handshake", false);
            SetNum(nuTries, IniInt("tries", 5));
            SetNum(nuOkSec, IniInt("okSec", 11));
            SetNum(nuResultSec, IniInt("resultSec", 120));
            SetNum(nuAutoCheck, IniInt("autoCheck", 0));
            ckKvAck.Checked = IniBool("kvAck", true);
            SetNum(nuAckDelay, IniInt("ackDelay", 0));
            tbAmount.Text = Ini("amount", "1.00");
            cbCurrency.Text = Ini("currency", "840 USD");
            SetNum(nuDecimals, IniInt("decimals", 2));
            tbRef.Text = Ini("ref", tbRef.Text);
            ckAutoRef.Checked = IniBool("autoRef", true);
            tbSeqNo.Text = Ini("seqNo", "");
            SetNum(nuRepeatSec, IniInt("repeatSec", 10));
            SetIdx(cbFraming, IniInt("framing", 0));
            ckEscapes.Checked = IniBool("escapes", true);
            ckHexIn.Checked = IniBool("hexIn", false);
            ckCrLf.Checked = IniBool("crlf", false);
            SetNum(nuFreeInterval, IniInt("freeInterval", 1000));
            tbFree.Text = Ini("freeText", "IsConnected?").Replace("\\n", "\r\n");
            ckLogHex.Checked = IniBool("logHex", false);
            RefreshHistory();
        }

        void SaveIni()
        {
            try
            {
                var s = ReadSettings();
                var d = new List<string>
                {
                    "# ECR Simulator settings (written on exit)",
                    "port=" + s.Port, "cable=" + cbCable.SelectedIndex, "baud=" + s.Baud, "data=" + cbData.SelectedIndex,
                    "parity=" + s.Parity, "stop=" + s.StopBits, "rts=" + cbRts.SelectedIndex, "dtr=" + cbDtr.SelectedIndex,
                    "perTxn=" + B01(s.PerTxn), "closeDelay=" + s.CloseDelayMs, "autoReopen=" + B01(s.AutoReopen),
                    "format=" + cbFormat.SelectedIndex, "handshake=" + B01(s.Handshake), "tries=" + s.OkRetries, "okSec=" + s.OkTimeoutSec,
                    "resultSec=" + s.ResultTimeoutSec, "autoCheck=" + s.AutoCheckSec, "kvAck=" + B01(s.KvAck), "ackDelay=" + s.KvAckDelayMs,
                    "amount=" + tbAmount.Text, "currency=" + cbCurrency.Text, "decimals=" + nuDecimals.Value, "ref=" + tbRef.Text,
                    "autoRef=" + B01(ckAutoRef.Checked), "seqNo=" + tbSeqNo.Text, "repeatSec=" + nuRepeatSec.Value,
                    "framing=" + cbFraming.SelectedIndex, "escapes=" + B01(ckEscapes.Checked), "hexIn=" + B01(ckHexIn.Checked),
                    "crlf=" + B01(ckCrLf.Checked), "freeInterval=" + nuFreeInterval.Value, "freeText=" + tbFree.Text.Replace("\r\n", "\\n").Replace("\n", "\\n"),
                    "logHex=" + B01(s.LogHex),
                };
                foreach (var h in history) d.Add("history=" + h);
                File.WriteAllLines(iniPath, d.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception) { /* settings are a convenience */ }
        }

        static string B01(bool b) { return b ? "1" : "0"; }

        // ===================== actions =====================

        void Txn(string type)
        {
            PushSettings();
            decimal amount;
            decimal.TryParse(tbAmount.Text.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
            string cur = Regex.Match(cbCurrency.Text, @"\d{3}").Value;
            var r = new TxnRequest
            {
                Type = type,
                Amount = amount,
                Currency = cur.Length == 3 ? cur : "840",
                Decimals = (int)nuDecimals.Value,
                Ref = tbRef.Text,
                OrigRef = tbOrigRef.Text,
                TxnId = tbTxnId.Text,
                SeqNo = tbSeqNo.Text,
                Repeat = (int)nuRepeat.Value,
                RepeatIntervalSec = (int)nuRepeatSec.Value,
            };
            engine.Start(r);
        }

        void SendFree()
        {
            byte[] data;
            try { data = BuildFree(); }
            catch (Exception e) { SetResult("FREE TEXT NOT SENT", e.Message, "bad"); return; }
            if (data.Length == 0) { SetResult("FREE TEXT NOT SENT", "Type something to send.", "bad"); return; }
            PushSettings();
            engine.SendRaw(data, (int)nuFreeRepeat.Value, (int)nuFreeInterval.Value);
            string entry = Framings[Math.Max(0, cbFraming.SelectedIndex)] + " | " + tbFree.Text.Replace("\r\n", " ").Replace("\n", " ");
            history.Remove(entry);
            history.Insert(0, entry);
            while (history.Count > 25) history.RemoveAt(history.Count - 1);
            RefreshHistory();
        }

        void RefreshHistory()
        {
            cbHistory.Items.Clear();
            cbHistory.Items.Add("History (" + history.Count + ")");
            foreach (var h in history) cbHistory.Items.Add(h);
            cbHistory.SelectedIndex = 0;
        }

        byte[] BuildFree()
        {
            var latin1 = Encoding.GetEncoding(28591);
            string text = tbFree.Text;
            string body;
            if (ckHexIn.Checked)
            {
                string hex = Regex.Replace(text, @"0x|[\s,;:-]", "", RegexOptions.IgnoreCase);
                if (hex.Length % 2 != 0 || !Regex.IsMatch(hex, "^[0-9a-fA-F]*$")) throw new FormatException("Hex input must be pairs of 0-9 / A-F, like 02 41 03.");
                var sb = new StringBuilder();
                for (int i = 0; i < hex.Length; i += 2) sb.Append((char)Convert.ToByte(hex.Substring(i, 2), 16));
                body = sb.ToString();
            }
            else
            {
                body = text.Replace("\r\n", "\n");
                if (ckEscapes.Checked) body = Unescape(body);
            }
            switch (cbFraming.SelectedIndex)
            {
                case 1: body = EcrEngine.QsFrame(body); break;
                case 2: body = "\x02" + body + "\x03\r\n"; break;
                case 3:
                    {
                        var fields = new List<string>();
                        foreach (var part in body.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string f = part.Trim().Trim('"');
                            if (f.Length > 0) fields.Add(f);
                        }
                        body = EcrEngine.KvFrame(fields);
                        break;
                    }
            }
            if (ckCrLf.Checked) body += "\r\n";
            return latin1.GetBytes(body);
        }

        static readonly string[] CtrlNames = { "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "TAB", "LF", "VT", "FF", "CR", "SO", "SI", "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US" };

        static string Unescape(string s)
        {
            s = Regex.Replace(s, @"<([A-Z]{2,3}|[0-9A-Fa-f]{2})>", delegate (Match m)
            {
                string n = m.Groups[1].Value;
                int i = Array.IndexOf(CtrlNames, n.ToUpperInvariant());
                if (i >= 0) return ((char)i).ToString();
                if (Regex.IsMatch(n, "^[0-9A-Fa-f]{2}$")) return ((char)Convert.ToByte(n, 16)).ToString();
                return m.Value;
            });
            s = Regex.Replace(s, @"\\x([0-9A-Fa-f]{2})", delegate (Match m) { return ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString(); });
            return s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");
        }

        void ShowAbout()
        {
            MessageBox.Show(this,
                "ECR Simulator " + Program.Version + "\n\n" +
                "Plays the cash register (ECR) against the Areeba POS:\n" +
                "  - QS, HDMC, KV and KV Jordan (progressive + delivery ack)\n" +
                "  - Nexgo D20 Bluetooth base or a direct serial cable\n" +
                "  - always-open or open-per-transaction (duty free / Mega Tech)\n" +
                "  - Sale, Preauth, Completion, Refund, Void, Settlement, connection check\n" +
                "  - free text, repeats, fault tests, full byte log\n\n" +
                "The port is opened through Win32 without touching RTS/DTR,\nso the D20 base does not reboot.\n\n" +
                "Created by " + Program.Author + ".",
                "About ECR Simulator", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void OpenFolder(string dir)
        {
            try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); } catch (Exception) { /* nothing to open */ }
        }

        // ===================== refresh =====================

        void SetResult(string title, string body, string kind)
        {
            lbResTitle.Text = title;
            lbResTitle.ForeColor = kind == "ok" ? OkColor : kind == "bad" ? BadColor : Color.Black;
            tbResult.Text = body;
        }

        void UpdateButtons()
        {
            var s = engine.GetSnapshot();
            bool hdmc = Format() == EcrFormat.HDMC;
            bool kv = Format() == EcrFormat.KV || Format() == EcrFormat.KVJordan;
            bool canStart = !s.Busy && s.RepeatLeft == 0;
            btSale.Enabled = btSettle.Enabled = btCheck.Enabled = canStart;
            btPreauth.Enabled = btCompletion.Enabled = btRefund.Enabled = btVoid.Enabled = canStart && !hdmc;
            btIncremental.Enabled = canStart && kv;
            btStop.Enabled = s.Busy || s.RepeatLeft > 0;
            bool perTxn = rbPerTxn.Checked;
            btConnect.Enabled = !perTxn && !s.Connected;
            btDisconnect.Enabled = s.Connected;
            tbTxnId.Enabled = tbSeqNo.Enabled = kv;
            ckKvAck.Enabled = nuAckDelay.Enabled = Format() == EcrFormat.KVJordan;
            nuCloseDelay.Enabled = perTxn;
            nuAutoCheck.Enabled = !perTxn;
            nuTries.Enabled = nuOkSec.Enabled = true;
            btCheck.Text = kv ? "Keep-alive" : "IsConnected?";
            lbStatus.Text = s.Connected ? "OPEN" : "CLOSED";
            lbStatus.BackColor = s.Connected ? OkColor : Color.Gray;
        }

        void Poll()
        {
            var logs = engine.TakeLogs();
            if (logs.Count > 0) AppendLogs(logs);
            var s = engine.GetSnapshot();
            if (s.Version != shownVersion)
            {
                shownVersion = s.Version;
                lbDisp1.Text = s.Display1;
                lbDisp2.Text = s.Display2;
                SetResult(s.ResultTitle, s.ResultBody, s.ResultKind);
                lbLast.Text = (s.LastApproved ?? "No approved sale yet.") + (s.LastPreauthRef != null ? "\nlast approved preauth: ref " + s.LastPreauthRef : "");
                if (s.LastSaleRef != null && s.LastSaleRef != seenSaleRef)
                {
                    seenSaleRef = s.LastSaleRef;
                    tbOrigRef.Text = s.LastSaleRef;
                    if (s.LastSaleTxnId != null) tbTxnId.Text = s.LastSaleTxnId;
                }
                if (s.LastPreauthRef != null && s.LastPreauthRef != seenPreauthRef)
                {
                    seenPreauthRef = s.LastPreauthRef;
                    tbOrigRef.Text = s.LastPreauthRef;
                }
                if (s.LastSentRef != null && s.LastSentRef != seenSentRef)
                {
                    seenSentRef = s.LastSentRef;
                    if (ckAutoRef.Checked) tbRef.Text = EcrEngine.NextRef(s.LastSentRef);
                }
            }
            lbStats.Text = s.Stats + (s.RepeatLeft > 0 ? "   |   repeats left " + s.RepeatLeft : "");
            UpdateButtons();
        }

        void AppendLogs(List<LogEntry> logs)
        {
            rtLog.SuspendLayout();
            foreach (var e in logs)
            {
                Color c = Color.FromArgb(90, 90, 90);
                if (e.Dir == "PC->POS") c = Color.FromArgb(0, 80, 170);
                else if (e.Dir == "POS->PC") c = Color.FromArgb(0, 120, 60);
                else if (e.Dir == "!!") c = BadColor;
                else if (e.Dir == "==") c = Color.FromArgb(120, 60, 0);
                else if (e.Dir == "hex") c = Color.FromArgb(140, 140, 140);
                rtLog.SelectionStart = rtLog.TextLength;
                rtLog.SelectionLength = 0;
                rtLog.SelectionColor = c;
                rtLog.AppendText(e.Time + "  " + e.Dir.PadRight(7) + "  " + EcrEngine.Show(e.Text) + "\n");
            }
            if (rtLog.Lines.Length > 6000)
            {
                rtLog.SelectionStart = 0;
                rtLog.SelectionLength = rtLog.GetFirstCharIndexFromLine(1000);
                rtLog.ReadOnly = false;
                rtLog.SelectedText = "";
                rtLog.ReadOnly = true;
            }
            rtLog.ResumeLayout();
            if (ckAutoScroll.Checked)
            {
                rtLog.SelectionStart = rtLog.TextLength;
                rtLog.ScrollToCaret();
            }
        }
    }
}
