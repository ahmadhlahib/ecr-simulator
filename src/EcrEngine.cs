// ECR Simulator - the cash register side of the Areeba POS ECR link.
// Created by Ahmad El Lahib.
//
// Speaks QS, HDMC, KV and KV Jordan (progressive) over the Nexgo D20 Bluetooth base or a direct serial cable.
// C# 5 only: it is compiled in memory by Windows PowerShell 5.1 (Add-Type) or by the .NET 4 csc in build.cmd.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace EcrSim
{
    public enum EcrFormat { QS, HDMC, KV, KVJordan }

    public enum LineMode { Off, On, Driver }

    // Serial port over Win32. Unlike System.IO.Ports.SerialPort it never pulses DTR/RTS on open:
    // the D20 base (ESP32-C3 native USB) reboots when RTS goes to 1 with DTR at 0.
    public sealed class RawCom : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommTimeouts(SafeFileHandle h, ref COMMTIMEOUTS t);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetCommState(SafeFileHandle h, ref DCB d);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommState(SafeFileHandle h, ref DCB d);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool EscapeCommFunction(SafeFileHandle h, uint func);
        const uint SETRTS = 3, CLRRTS = 4, SETDTR = 5, CLRDTR = 6;

        [StructLayout(LayoutKind.Sequential)]
        struct COMMTIMEOUTS { public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant, WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant; }

        [StructLayout(LayoutKind.Sequential)]
        struct DCB
        {
            public uint DCBlength, BaudRate, Flags;
            public ushort wReserved, XonLim, XoffLim;
            public byte ByteSize, Parity, StopBits;
            public sbyte XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
            public ushort wReserved1;
        }

        readonly SafeFileHandle h;
        public readonly string Before = "";

        public RawCom(string port, Settings s, int writeTimeoutMs)
        {
            h = CreateFile(@"\\.\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "open " + port);
            var d = new DCB();
            d.DCBlength = (uint)Marshal.SizeOf(typeof(DCB));
            if (!GetCommState(h, ref d)) { int e = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(e, "GetCommState"); }
            Before = string.Format(CultureInfo.InvariantCulture, "baud={0} dtrControl={1} rtsControl={2}", d.BaudRate, (d.Flags >> 4) & 3, (d.Flags >> 12) & 3);
            d.BaudRate = (uint)s.Baud;
            d.ByteSize = (byte)s.DataBits;
            d.Parity = (byte)s.Parity;
            d.StopBits = (byte)s.StopBits;
            uint f = d.Flags | 1u;                              // fBinary
            f = s.Parity != 0 ? f | 2u : f & ~2u;               // fParity
            f &= ~(4u | 8u | 64u | 256u | 512u);                // no CTS/DSR/XON-XOFF flow control
            if (s.Dtr != LineMode.Driver) f = (f & ~(3u << 4)) | ((s.Dtr == LineMode.On ? 1u : 0u) << 4);
            if (s.Rts != LineMode.Driver) f = (f & ~(3u << 12)) | ((s.Rts == LineMode.On ? 1u : 0u) << 12);
            d.Flags = f;
            if (!SetCommState(h, ref d)) { int e = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(e, "SetCommState"); }
            var t = new COMMTIMEOUTS { ReadIntervalTimeout = 0xFFFFFFFF, WriteTotalTimeoutConstant = (uint)writeTimeoutMs };
            SetCommTimeouts(h, ref t);  // reads return at once with what is waiting
        }

        public void Write(byte[] b)
        {
            int w;
            if (!WriteFile(h, b, b.Length, out w, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "write");
            if (w != b.Length) throw new TimeoutException("write: only " + w + " of " + b.Length + " bytes taken (timeout)");
        }

        public byte[] Read()
        {
            var b = new byte[4096];
            int r;
            if (!ReadFile(h, b, b.Length, out r, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "read");
            var o = new byte[r];
            Array.Copy(b, o, r);
            return o;
        }

        public void SetRts(bool on) { if (!EscapeCommFunction(h, on ? SETRTS : CLRRTS)) throw new Win32Exception(Marshal.GetLastWin32Error(), "RTS"); }
        public void SetDtr(bool on) { if (!EscapeCommFunction(h, on ? SETDTR : CLRDTR)) throw new Win32Exception(Marshal.GetLastWin32Error(), "DTR"); }

        public void Dispose() { h.Dispose(); }
    }

    public sealed class Settings
    {
        public string Port = "";
        public int Baud = 115200, DataBits = 8, Parity = 0, StopBits = 0;   // StopBits: 0 = 1, 1 = 1.5, 2 = 2
        public LineMode Rts = LineMode.Off, Dtr = LineMode.Off;
        public EcrFormat Format = EcrFormat.QS;
        public bool PerTxn = true;          // duty free / Mega Tech: open the port per transaction, close it after the result
        public int CloseDelayMs = 0;
        public bool AutoReopen = true;
        public bool Handshake = false;      // IsConnected? (QS/HDMC) or keep-alive (KV) before each transaction
        public int OkRetries = 5, OkTimeoutSec = 11, ResultTimeoutSec = 120;
        public int AutoCheckSec = 0;        // connection check every N s while idle and open (0 = off)
        public bool KvAck = true;           // KV Jordan: answer an approved sale with "<START>","<END>" (else the POS reverses it)
        public int KvAckDelayMs = 0;
        public bool LogHex = false;

        public Settings Clone() { return (Settings)MemberwiseClone(); }
    }

    public sealed class TxnRequest
    {
        public string Type;                 // Sale, Preauth, Completion, Refund, Void, Settlement
        public decimal Amount;
        public string Currency = "840";
        public int Decimals = 2;
        public string Ref, OrigRef, TxnId, SeqNo;
        public int Repeat = 1, RepeatIntervalSec = 10;

        public TxnRequest Clone() { return (TxnRequest)MemberwiseClone(); }
    }

    public sealed class LogEntry
    {
        public string Time, Dir, Text;
    }

    public sealed class Snapshot
    {
        public bool Connected, Busy;
        public string Port, Display1, Display2, ResultTitle, ResultBody, ResultKind, Stats;
        public string LastSaleRef, LastSaleTxnId, LastPreauthRef, LastApproved, LastSentRef;
        public int RepeatLeft;
        public long Version;
    }

    public sealed class EcrEngine : IDisposable
    {
        const char STX = '\x02', ETX = '\x03';
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        const string KvStart = "\"<START>\"", KvEnd = "\"<END>\"";

        sealed class Pending { public TxnRequest Req; public string Frame, Title; public bool CheckOnly; }

        readonly object gate = new object();
        readonly Queue<Action> commands = new Queue<Action>();
        readonly List<LogEntry> newLogs = new List<LogEntry>();
        readonly Thread worker;
        volatile bool running = true;
        StreamWriter logWriter;
        public readonly string LogFile;

        Settings cfg = new Settings();
        RawCom sp;
        readonly StringBuilder rx = new StringBuilder();
        DateTime lastRx = DateTime.MinValue;

        string state = "idle";              // idle | handshake | result
        Pending pending;
        TxnRequest lastSale, lastPreauth;
        string lastSaleTxnId, lastSentRef;
        DateTime t0 = DateTime.Now;
        int tries, repeatLeft;
        TxnRequest repeatReq;
        DateTime closeAt = DateTime.MaxValue, nextRepeatAt = DateTime.MaxValue, pauseUntil = DateTime.MinValue;
        DateTime reopenAt = DateTime.MaxValue, ackAt = DateTime.MaxValue, nextAutoCheck = DateTime.MaxValue;

        int okMissing, writeBlocked, portLost, baseReboots, approved, declined, noResult;
        readonly List<double> okTimes = new List<double>();

        string disp1 = "READY", disp2 = "", resTitle = "", resBody = "", resKind = "";
        long version;

        public EcrEngine(string logDir)
        {
            Directory.CreateDirectory(logDir);
            LogFile = Path.Combine(logDir, "ecr-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            try
            {
                // Shared read/write so a tail -f on the log never blocks the writes.
                logWriter = new StreamWriter(new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                logWriter.AutoFlush = true;
            }
            catch (Exception) { logWriter = null; }
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Name = "EcrEngine";
            worker.Start();
        }

        // ===================== public API (UI thread) =====================

        public void Apply(Settings s) { var c = s.Clone(); Enqueue(delegate { ApplySettings(c); }); }
        public void Connect() { Enqueue(delegate { closeAt = DateTime.MaxValue; if (sp == null) OpenPort(); }); }
        public void Disconnect() { Enqueue(delegate { repeatLeft = 0; nextRepeatAt = DateTime.MaxValue; ClosePort("by the user"); Display("DISCONNECTED", ""); }); }
        public void Check() { Enqueue(delegate { StartCheck(); }); }
        public void Start(TxnRequest r) { var c = r.Clone(); Enqueue(delegate { repeatReq = c; repeatLeft = Math.Max(0, c.Repeat - 1); StartTxn(c); }); }
        public void Stop()
        {
            Enqueue(delegate
            {
                repeatLeft = 0;
                nextRepeatAt = DateTime.MaxValue;
                if (state == "idle") return;
                Log("--", "stopped waiting (by the user)");
                GiveUp("STOPPED", "Stopped waiting. The terminal may still finish the transaction; its messages stay in the log.");
            });
        }
        public void SendRaw(byte[] data, int repeat, int intervalMs) { Enqueue(delegate { SendFreeText(data, repeat, intervalMs); }); }
        public void PauseReading(int sec) { Enqueue(delegate { pauseUntil = DateTime.Now.AddSeconds(sec); Log("--", "TEST: not reading the port for " + sec + " s (frozen register)"); }); }
        public void ClosePortFor(int sec)
        {
            Enqueue(delegate
            {
                if (sp == null) { Log("!!", "TEST: the port is not open"); return; }
                ClosePort("TEST: closed for " + sec + " s");
                reopenAt = DateTime.Now.AddSeconds(sec);
            });
        }
        public void ResetStats() { Enqueue(delegate { okTimes.Clear(); okMissing = writeBlocked = portLost = baseReboots = approved = declined = noResult = 0; Changed(); }); }

        public List<LogEntry> TakeLogs()
        {
            lock (gate)
            {
                var l = new List<LogEntry>(newLogs);
                newLogs.Clear();
                return l;
            }
        }

        public Snapshot GetSnapshot()
        {
            lock (gate)
            {
                return new Snapshot
                {
                    Connected = sp != null,
                    Busy = state != "idle",
                    Port = cfg.Port,
                    Display1 = disp1,
                    Display2 = disp2,
                    ResultTitle = resTitle,
                    ResultBody = resBody,
                    ResultKind = resKind,
                    Stats = StatsText(),
                    LastSaleRef = lastSale == null ? null : lastSale.Ref,
                    LastSaleTxnId = lastSaleTxnId,
                    LastPreauthRef = lastPreauth == null ? null : lastPreauth.Ref,
                    LastApproved = lastSale == null ? null : string.Format(CultureInfo.InvariantCulture, "last approved sale: ref {0}  {1} {2}{3}",
                        lastSale.Ref, FormatAmount(lastSale.Amount, lastSale.Decimals), CurrencyName(lastSale.Currency),
                        lastSaleTxnId == null ? "" : "  TXNID " + lastSaleTxnId),
                    LastSentRef = lastSentRef,
                    RepeatLeft = repeatLeft,
                    Version = version,
                };
            }
        }

        public void Dispose()
        {
            running = false;
            worker.Join(2000);
            lock (gate)
            {
                ClosePort("simulator closed");
                if (logWriter != null) logWriter.Dispose();
            }
        }

        // COM ports with a readable label; the Nexgo D20 base is the Espressif USB device VID_303A.
        public static List<KeyValuePair<string, string>> Ports()
        {
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    if (usb != null)
                        foreach (var dev in usb.GetSubKeyNames())
                            using (var dk = usb.OpenSubKey(dev))
                            {
                                if (dk == null) continue;
                                foreach (var inst in dk.GetSubKeyNames())
                                    using (var ik = dk.OpenSubKey(inst))
                                    using (var pk = dk.OpenSubKey(inst + @"\Device Parameters"))
                                    {
                                        var n = pk == null ? null : pk.GetValue("PortName") as string;
                                        if (n == null) continue;
                                        bool d20 = dev.StartsWith("VID_303A", StringComparison.OrdinalIgnoreCase);
                                        string name = ik == null ? null : ik.GetValue("FriendlyName") as string;
                                        if (name != null) name = Regex.Replace(name, @"\s*\(COM\d+\)\s*$", "");
                                        labels[n] = d20 ? "Nexgo D20 base (Bluetooth)" : (name ?? "USB serial");
                                    }
                            }
                }
            }
            catch (Exception) { /* registry not readable: plain port names */ }
            var names = new List<string>(new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase));
            names.Sort(delegate (string x, string y) { return PortNumber(x).CompareTo(PortNumber(y)); });
            var list = new List<KeyValuePair<string, string>>();
            foreach (var n in names)
            {
                string l;
                list.Add(new KeyValuePair<string, string>(n, labels.TryGetValue(n, out l) ? n + "  -  " + l : n));
            }
            return list;
        }

        public static bool IsD20(string label) { return label != null && label.Contains("D20"); }

        // ===================== worker =====================

        void Enqueue(Action a) { lock (gate) commands.Enqueue(a); }

        void Loop()
        {
            while (running)
            {
                Action a = null;
                lock (gate)
                {
                    if (commands.Count > 0) a = commands.Dequeue();
                    try
                    {
                        if (a != null) a();
                        else Tick();
                    }
                    catch (Exception e) { Log("!!", "simulator error: " + e.Message); }
                }
                if (a == null) Thread.Sleep(30);
            }
        }

        void Tick()
        {
            var now = DateTime.Now;
            if (sp != null && now >= pauseUntil)
            {
                if (pauseUntil != DateTime.MinValue) { pauseUntil = DateTime.MinValue; Log("--", "TEST: reading the port again"); }
                foreach (var m in ReadMessages()) OnMessage(m);
            }
            if (now >= ackAt) { ackAt = DateTime.MaxValue; SendKvAck(); }
            if (state == "handshake" && (now - t0).TotalSeconds >= cfg.OkTimeoutSec)
            {
                okMissing++;
                Log("!!", "no answer to the connection check within " + cfg.OkTimeoutSec + " s");
                if (tries < cfg.OkRetries) AskConnected();
                else GiveUp("NO ANSWER", "The terminal did not answer the connection check " + tries + " times.\nIs the ECR screen open on the POS and the format the same (" + FormatName(cfg.Format) + ")?");
            }
            if (state == "result" && (now - t0).TotalSeconds >= cfg.ResultTimeoutSec)
            {
                noResult++;
                Log("!!", "NO result within " + cfg.ResultTimeoutSec + " s");
                GiveUp("NO RESULT", "No final answer from the terminal within " + cfg.ResultTimeoutSec + " s.");
            }
            if (state == "idle" && now >= closeAt && ackAt == DateTime.MaxValue)
            {
                closeAt = DateTime.MaxValue;
                if (cfg.PerTxn && sp != null) ClosePort("transaction finished (per-transaction mode)");
            }
            if (now >= reopenAt)
            {
                reopenAt = DateTime.MaxValue;
                Log("--", "TEST: reopening the port");
                OpenPort();
            }
            if (state == "idle" && now >= nextRepeatAt)
            {
                nextRepeatAt = DateTime.MaxValue;
                if (repeatReq != null && repeatLeft > 0)
                {
                    repeatLeft--;
                    var r = repeatReq.Clone();
                    r.Ref = NextRef(r.Ref);
                    repeatReq.Ref = r.Ref;
                    StartTxn(r);
                }
            }
            if (cfg.AutoCheckSec > 0 && state == "idle" && sp != null && !cfg.PerTxn)
            {
                if (nextAutoCheck == DateTime.MaxValue) nextAutoCheck = now.AddSeconds(cfg.AutoCheckSec);
                else if (now >= nextAutoCheck) { nextAutoCheck = DateTime.MaxValue; StartCheck(); }
            }
            else nextAutoCheck = DateTime.MaxValue;
        }

        // ===================== log / helpers =====================

        void Changed() { version++; }

        public static string Show(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == STX) sb.Append("<STX>");
                else if (c == ETX) sb.Append("<ETX>");
                else if (c == '\r') sb.Append("<CR>");
                else if (c == '\n') sb.Append("<LF>");
                else if (c == '\x06') sb.Append("<ACK>");
                else if (c == '\x15') sb.Append("<NAK>");
                else if (c == '\x04') sb.Append("<EOT>");
                else if (c < ' ' || (c >= '\x7f' && c < '\xa0')) sb.Append("<").Append(((int)c).ToString("X2")).Append(">");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder();
            foreach (var x in b) sb.Append(x.ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        void Log(string dir, string text)
        {
            var e = new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), Dir = dir, Text = text };
            newLogs.Add(e);
            if (newLogs.Count > 20000) newLogs.RemoveRange(0, 5000);
            if (logWriter != null)
                try { logWriter.WriteLine(e.Time + "  " + dir.PadRight(7) + "  " + Show(text)); } catch (Exception) { /* the window still shows it */ }
        }

        static string Lrc(string body) { int x = 0; foreach (char c in body) x ^= c; return ((char)x).ToString(); }

        // Register -> POS QS/HDMC frame: STX text ETX LRC (LRC = XOR of the text and ETX).
        public static string QsFrame(string text) { string body = text + ETX; return STX + body + Lrc(body); }

        public static string KvFrame(IEnumerable<string> fields)
        {
            var sb = new StringBuilder(KvStart);
            foreach (var f in fields) sb.Append(",\"").Append(f).Append('"');
            return sb.Append(',').Append(KvEnd).ToString();
        }

        public static string CurrencyName(string code)
        {
            switch (code)
            {
                case "840": return "USD";
                case "422": return "LBP";
                case "400": return "JOD";
                case "368": return "IQD";
                case "978": return "EUR";
                case "784": return "AED";
                case "682": return "SAR";
                case "818": return "EGP";
                default: return code;
            }
        }

        static string FormatAmount(decimal a, int dec) { return a.ToString("F" + dec, CultureInfo.InvariantCulture); }

        static int PortNumber(string n) { int v; return int.TryParse(n.Replace("COM", ""), out v) ? v : int.MaxValue; }

        public static string FormatName(EcrFormat f) { return f == EcrFormat.KVJordan ? "KV Jordan (progressive)" : f.ToString(); }

        static string Digits(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (c >= '0' && c <= '9') sb.Append(c);
            return sb.ToString();
        }

        static string Ref8(string s)
        {
            string d = Digits(s);
            if (d.Length == 0) d = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
            return d.Length > 8 ? d.Substring(d.Length - 8) : d.PadLeft(8, '0');
        }

        public static string NextRef(string r)
        {
            long v;
            if (!long.TryParse(Digits(r), out v)) v = 0;
            return ((v + 1) % 100000000).ToString("00000000", CultureInfo.InvariantCulture);
        }

        void Display(string l1, string l2) { if (l1 != null) disp1 = l1; if (l2 != null) disp2 = l2; Changed(); }

        // kind: ok | bad | "" (neutral)
        void Result(string title, string body, string kind) { resTitle = title; resBody = body; resKind = kind; Changed(); }

        string StatsText()
        {
            string s = string.Format(CultureInfo.InvariantCulture, "Approved {0}   declined/cancelled {1}   no result {2}", approved, declined, noResult);
            if (okTimes.Count > 0)
            {
                var t = new List<double>(okTimes);
                t.Sort();
                s += string.Format(CultureInfo.InvariantCulture, "   |   checks ok {0}  median {1:N0} ms  max {2:N0} ms", t.Count, t[t.Count / 2], t[t.Count - 1]);
            }
            if (okMissing > 0) s += "   missing " + okMissing;
            if (writeBlocked > 0) s += "   PC writes blocked " + writeBlocked;
            if (portLost > 0) s += "   dead port " + portLost;
            if (baseReboots > 0) s += "   D20 reboots " + baseReboots;
            return s;
        }

        string LinesText()
        {
            return "RTS=" + LineText(cfg.Rts) + " DTR=" + LineText(cfg.Dtr) +
                   (cfg.Rts == LineMode.On && cfg.Dtr != LineMode.On ? "  (a D20 base REBOOTS with RTS=1 DTR=0)" : "");
        }

        static string LineText(LineMode m) { return m == LineMode.On ? "1" : m == LineMode.Off ? "0" : "driver"; }

        string PortText()
        {
            string par = "NOEMS".Substring(Math.Max(0, Math.Min(4, cfg.Parity)), 1);
            string stop = cfg.StopBits == 0 ? "1" : cfg.StopBits == 1 ? "1.5" : "2";
            return cfg.Port + " @ " + cfg.Baud + " " + cfg.DataBits + par + stop;
        }

        // ===================== settings / port =====================

        void ApplySettings(Settings s)
        {
            var old = cfg;
            cfg = s;
            if (sp != null)
            {
                if (s.Rts != old.Rts && s.Rts != LineMode.Driver) SetLine("RTS", s.Rts == LineMode.On);
                if (s.Dtr != old.Dtr && s.Dtr != LineMode.Driver) SetLine("DTR", s.Dtr == LineMode.On);
                if (s.Port != old.Port || s.Baud != old.Baud || s.DataBits != old.DataBits || s.Parity != old.Parity || s.StopBits != old.StopBits)
                    Log("--", "port settings changed: they apply the next time the port opens (Disconnect, then Connect)");
            }
            if (s.PerTxn != old.PerTxn)
            {
                if (s.PerTxn)
                {
                    if (sp != null && state == "idle") ClosePort("per-transaction mode on");
                    Log("--", "mode: open the port per transaction, close it after the result (duty free / Mega Tech)");
                    Display("READY", "The port opens when you start a transaction");
                }
                else
                {
                    closeAt = DateTime.MaxValue;
                    Log("--", "mode: always open (press Connect)");
                    Display(sp == null ? "NOT CONNECTED" : "READY", sp == null ? "Press Connect" : "");
                }
            }
            if (s.Format != old.Format)
            {
                rx.Length = 0;
                Log("--", "format: " + FormatName(s.Format));
            }
        }

        void SetLine(string name, bool on)
        {
            try
            {
                if (name == "RTS") sp.SetRts(on); else sp.SetDtr(on);
                Log("--", name + " set to " + (on ? 1 : 0) + " on the open port -> " + LinesText());
            }
            catch (Exception e) { Log("!!", "could not set " + name + ": " + e.Message); }
        }

        bool OpenPort()
        {
            if (string.IsNullOrEmpty(cfg.Port)) { Log("!!", "no COM port selected"); Result("NO PORT", "Pick a COM port first.", "bad"); return false; }
            try
            {
                sp = new RawCom(cfg.Port, cfg, 3000);
                rx.Length = 0;
                Log("--", "opened " + PortText() + " " + LinesText() + "  (driver had: " + sp.Before + ")");
                Display("READY", "Port open");
                Changed();
                return true;
            }
            catch (Exception e)
            {
                sp = null;
                bool present = Array.IndexOf(SerialPort.GetPortNames(), cfg.Port) >= 0;
                Log("!!", "could not open " + cfg.Port + ": " + e.Message + (present ? "" : "  -> the port does not exist right now"));
                Result("PORT NOT OPENED", cfg.Port + ": " + e.Message + "\n" + (present
                    ? "Only one program can use a COM port at a time. Close other simulators, terminals tools or the register software."
                    : cfg.Port + " does not exist right now: the D20 or the cable is unplugged, powered off or rebooting."), "bad");
                Display("PORT NOT OPENED", present ? "port busy" : "port not found");
                return false;
            }
        }

        // Per-transaction mode opens the port before a transaction. Returns false when it is not open.
        bool EnsureOpen()
        {
            closeAt = DateTime.MaxValue;
            if (sp != null) return true;
            if (!cfg.PerTxn) { Result("NOT CONNECTED", "Press Connect first, or choose \"Open per transaction\".", "bad"); Display("NOT CONNECTED", "Press Connect"); return false; }
            return OpenPort();
        }

        void ScheduleClose(int extraMs)
        {
            if (cfg.PerTxn && sp != null) closeAt = DateTime.Now.AddMilliseconds(Math.Max(0, cfg.CloseDelayMs) + extraMs);
        }

        void ClosePort(string why)
        {
            if (sp != null)
            {
                try { sp.Dispose(); } catch (Exception) { /* dead handle */ }
                sp = null;
                Log("--", "closed " + cfg.Port + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")"));
            }
            pending = null;
            state = "idle";
            closeAt = DateTime.MaxValue;
            ackAt = DateTime.MaxValue;
            Changed();
        }

        bool Write(string data, string note)
        {
            var bytes = Latin1.GetBytes(data);
            var t = DateTime.Now;
            try
            {
                sp.Write(bytes);
                int ms = (int)(DateTime.Now - t).TotalMilliseconds;
                Log("PC->POS", data + (note == null ? "" : "   (" + note + ")") + (ms > 200 ? "   (write took " + ms + " ms)" : ""));
                if (cfg.LogHex) Log("hex", Hex(bytes));
                return true;
            }
            catch (Win32Exception e)
            {
                // Windows rejected the write: the handle is dead because the device was unplugged, powered off
                // or rebooted since the port was opened (it comes back as a new COM port instance).
                portLost++;
                Log("!!", "PORT DEAD (stale COM port): Windows error " + e.NativeErrorCode + " '" + new Win32Exception(e.NativeErrorCode).Message + "'");
                if (cfg.AutoReopen && ReopenPort())
                {
                    try { sp.Write(bytes); Log("PC->POS", data); return true; }
                    catch (Exception e2) { Log("!!", "write failed again after reopening: " + e2.Message); }
                }
                Result("PORT DEAD (stale COM port)", "The device was unplugged, powered off or rebooted since " + cfg.Port + " was opened.\nConnect again (or turn on \"Auto-reopen a dead port\").", "bad");
                Display("PORT DEAD", "Reconnect the COM port");
                ClosePort("dead handle");
                return false;
            }
            catch (Exception e)
            {
                // Only part of the data went out before the write timeout: the other side is not taking data.
                int ms = (int)(DateTime.Now - t).TotalMilliseconds;
                writeBlocked++;
                Log("!!", "PC WRITE BLOCKED after " + ms + " ms (the other side did not take the data): " + e.Message);
                return false;
            }
        }

        bool ReopenPort()
        {
            try { if (sp != null) sp.Dispose(); } catch (Exception) { /* dead handle */ }
            sp = null;
            try
            {
                sp = new RawCom(cfg.Port, cfg, 3000);
                rx.Length = 0;
                Log("--", "reopened " + PortText());
                return true;
            }
            catch (Exception e) { Log("!!", "could not reopen " + cfg.Port + ": " + e.Message); return false; }
        }

        // ===================== reading =====================

        bool IsKv { get { return cfg.Format == EcrFormat.KV || cfg.Format == EcrFormat.KVJordan; } }

        // Reads what is waiting and returns complete POS messages.
        List<string> ReadMessages()
        {
            var output = new List<string>();
            try
            {
                while (sp != null)
                {
                    var chunk = sp.Read();
                    if (chunk.Length == 0) break;
                    if (cfg.LogHex) Log("hex", "read " + chunk.Length + " B: " + Hex(chunk));
                    rx.Append(Latin1.GetString(chunk));
                    lastRx = DateTime.Now;
                }
            }
            catch (Exception e)
            {
                portLost++;
                Log("!!", "PORT LOST while reading: " + e.Message);
                Result("PORT LOST", "The COM port closed (the device rebooted or the cable came out). Connect again.", "bad");
                Display("PORT LOST", "");
                ClosePort("port lost");
                return output;
            }
            string text = rx.ToString();
            int boot = text.IndexOf("ESP-ROM", StringComparison.Ordinal);
            if (boot >= 0)
            {
                var m = Regex.Match(text, @"rst:(0x[0-9a-fA-F]+ \([^)]*\))");
                Log("!!", "D20 BASE REBOOTED - reset reason: " + (m.Success ? m.Groups[1].Value : "not captured"));
                baseReboots++;
                rx.Length = 0;
                return output;
            }
            if (IsKv) SplitKv(output); else SplitStxEtx(output);
            // Leftover bytes that never become a message (wrong format or baud rate): show them after 1.5 s.
            if (rx.Length > 0 && (DateTime.Now - lastRx).TotalMilliseconds > 1500)
            {
                Log("POS->PC", rx + "   (incomplete: no " + (IsKv ? "\"<END>\"" : "ETX") + " - wrong format or baud rate?)");
                rx.Length = 0;
            }
            return output;
        }

        void SplitStxEtx(List<string> output)
        {
            while (true)
            {
                string s = rx.ToString();
                int i = s.IndexOf(ETX);
                if (i < 0) break;
                string msg = s.Substring(0, i + 1).TrimStart('\r', '\n');
                rx.Remove(0, i + 1);
                // The POS ends each message with CR LF after ETX; drop it so the next message starts clean.
                while (rx.Length > 0 && (rx[0] == '\r' || rx[0] == '\n')) rx.Remove(0, 1);
                Log("POS->PC", msg);
                output.Add(msg);
            }
        }

        // KV Jordan sends a message progressively (growing prefixes 300 ms apart, then the whole message):
        // only the text from the last "<START>" before each "<END>" is the message.
        void SplitKv(List<string> output)
        {
            while (true)
            {
                string s = rx.ToString();
                int e = s.IndexOf(KvEnd, StringComparison.Ordinal);
                if (e < 0) break;
                int end = e + KvEnd.Length;
                int start = s.LastIndexOf(KvStart, e, StringComparison.Ordinal);
                if (start < 0) start = 0;
                if (start > 0) Log("POS->PC", s.Substring(0, start) + "   (progressive chunks, " + start + " bytes)");
                string msg = s.Substring(start, end - start);
                rx.Remove(0, end);
                while (rx.Length > 0 && (rx[0] == '\r' || rx[0] == '\n')) rx.Remove(0, 1);
                Log("POS->PC", msg);
                output.Add(msg);
            }
        }

        // ===================== protocol =====================

        public static Dictionary<string, string> Fields(string m, bool kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] parts = kv
                ? m.Replace("\"", "").Split(',')
                : m.Trim(STX, ETX).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                string line = raw.Trim();
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            return d;
        }

        static string Get(Dictionary<string, string> f, string k) { string v; return f.TryGetValue(k, out v) && v.Length > 0 ? v : "-"; }

        static string Plain(string m) { return m.Replace(STX, ' ').Replace(ETX, ' ').Replace('\r', ' ').Replace('\n', ' ').Trim(); }

        string CheckFrame() { return IsKv ? KvFrame(new[] { "TXNID=0" }) : "IsConnected?"; }

        void StartCheck()
        {
            if (state != "idle") { Log("!!", "busy: a transaction is in progress"); return; }
            if (!EnsureOpen()) return;
            pending = new Pending { CheckOnly = true, Title = "CONNECTION CHECK" };
            tries = 0;
            Result("", "", "");
            AskConnected();
        }

        void AskConnected()
        {
            tries++;
            t0 = DateTime.Now;
            state = "handshake";
            Display(pending != null && !pending.CheckOnly ? pending.Title : "CHECKING", (IsKv ? "Keep-alive" : "IsConnected?") + " (try " + tries + " of " + cfg.OkRetries + ")");
            if (!Write(CheckFrame(), IsKv ? "KV keep-alive" : null) && state != "idle") GiveUp("PC WRITE BLOCKED", "The connection check could not be written to the port.");
        }

        void StartTxn(TxnRequest r)
        {
            if (state != "idle") { Log("!!", "busy: a transaction is in progress"); return; }
            string title, frame = BuildFrame(r, out title);
            if (frame == null) return;
            if (!EnsureOpen()) { repeatLeft = 0; return; }
            pending = new Pending { Req = r, Frame = frame, Title = title };
            if (r.Type == "Sale" || r.Type == "Preauth" || r.Type == "Refund") lastSentRef = r.Ref;
            Result("", "", "");
            Display(title, "");
            if (cfg.Handshake) { tries = 0; AskConnected(); } else SendPending();
        }

        void SendPending()
        {
            t0 = DateTime.Now;
            state = "result";
            Display(pending.Title, "Waiting for the terminal...");
            if (!Write(pending.Frame, null) && state != "idle") GiveUp("PC WRITE BLOCKED", "The transaction could not be written to the port.");
        }

        void GiveUp(string title, string body)
        {
            Result(title, body, "bad");
            Display(title, "");
            EndTxn();
        }

        void EndTxn()
        {
            pending = null;
            state = "idle";
            ScheduleClose(0);
            if (repeatLeft > 0) nextRepeatAt = DateTime.Now.AddSeconds(Math.Max(1, repeatReq.RepeatIntervalSec));
            Changed();
        }

        // Builds the register's request for the selected format. Returns null (and shows why) when it cannot.
        string BuildFrame(TxnRequest r, out string title)
        {
            title = r.Type.ToUpperInvariant();
            bool money = r.Type != "Settlement" && r.Type != "Void";
            if (money && r.Amount <= 0) { Result("ENTER AN AMOUNT", "Enter an amount like 12.50", "bad"); return null; }
            if (cfg.Format == EcrFormat.HDMC && r.Type != "Sale" && r.Type != "Settlement")
            {
                Result("NOT IN HDMC", "HDMC only has Sale, Settlement and IsConnected?.", "bad");
                return null;
            }
            bool byOriginal = r.Type == "Void" || r.Type == "Completion";
            string rf = Ref8(byOriginal && !string.IsNullOrEmpty(Digits(r.OrigRef)) ? r.OrigRef : r.Ref);
            if (byOriginal && string.IsNullOrEmpty(Digits(r.OrigRef)) && !(IsKv && r.Type == "Void"))
            {
                Result("ORIGINAL REF MISSING", "Enter the ECR ref of the original " + (r.Type == "Void" ? "sale" : "preauth") + " in \"Original ref\".", "bad");
                return null;
            }
            if (IsKv && r.Type == "Void" && string.IsNullOrEmpty(Digits(r.TxnId)))
            {
                Result("TXNID MISSING", "KV voids find the sale by TXNID (the STAN in the sale's response). Enter it in \"KV TXNID\".", "bad");
                return null;
            }
            r.Ref = rf;
            string amountText = FormatAmount(r.Amount, r.Decimals);
            if (money) title += "  " + amountText + " " + CurrencyName(r.Currency);
            if (byOriginal) title += "  ref " + rf;

            if (!IsKv)
            {
                if (r.Type == "Settlement") return QsFrame("InitiateTxn=Settlement");
                int dec = cfg.Format == EcrFormat.HDMC ? 2 : r.Decimals;    // the POS reads HDMC amounts with 2 decimals
                decimal scaled = Math.Round(r.Amount * (decimal)Math.Pow(10, dec));
                string minor = ((long)scaled).ToString("000000000000", CultureInfo.InvariantCulture);
                if (minor.Length > 12) { Result("AMOUNT TOO BIG", "The amount does not fit in 12 digits.", "bad"); return null; }
                if (cfg.Format == EcrFormat.HDMC) return QsFrame("InitiateTxn=Sale&" + rf + minor);
                return QsFrame("InitiateTxn=" + r.Type + "&" + rf + minor + r.Currency);
            }

            var f = new List<string>();
            switch (r.Type)
            {
                case "Settlement": f.Add("CMD=SETTLE"); break;
                case "Void":
                    f.Add("CMD=VOID");
                    f.Add("TXNID=" + Digits(r.TxnId));
                    if (!string.IsNullOrEmpty(Digits(r.OrigRef))) f.Add("ECRREF=" + rf);
                    break;
                default:
                    f.Add("CMD=" + r.Type.ToUpperInvariant());
                    f.Add("AMT=" + amountText);
                    f.Add("CURRENCY=" + r.Currency);
                    f.Add("ECRREF=" + rf);
                    break;
            }
            if (!string.IsNullOrEmpty(r.SeqNo)) f.Add("TXNCONSEQUENTIALNO=" + r.SeqNo.Trim());
            return KvFrame(f);
        }

        void OnMessage(string m)
        {
            if (IsKv) OnKvMessage(m); else OnQsMessage(m);
        }

        void HandshakeOk()
        {
            double ms = (DateTime.Now - t0).TotalMilliseconds;
            okTimes.Add(ms);
            Log("--", string.Format(CultureInfo.InvariantCulture, "connection ok after {0:N0} ms", ms));
            if (pending != null && !pending.CheckOnly) { SendPending(); return; }
            Result(string.Format(CultureInfo.InvariantCulture, "TERMINAL CONNECTED  ({0:N0} ms)", ms),
                IsKv ? "The POS answered the keep-alive with an empty frame." : "The POS answered IsConnected? with ok.", "ok");
            Display("READY", string.Format(CultureInfo.InvariantCulture, "connected, answer in {0:N0} ms", ms));
            EndTxn();
        }

        void OnQsMessage(string m)
        {
            string t = m.Trim(STX, ETX, '\r', '\n', ' ');
            if (t.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                if (state == "handshake") HandshakeOk();
                return;
            }
            if (t == "Idle-State") return;
            bool final = t.StartsWith("TxnResp", StringComparison.Ordinal) || t.StartsWith("SettlementResp", StringComparison.Ordinal) ||
                         t == "Cancelled" || t == "Connect Failed" || t == "Batch Empty";
            if (!final) { Display(null, t); return; }
            if (state != "result" || pending == null)
            {
                Log("--", "(final message outside a transaction)");
                return;
            }
            var f = Fields(m, false);
            string got;
            // A result left in the base from an earlier transaction comes out when the port opens again:
            // only accept the result that carries our ECR ref.
            if (cfg.Format == EcrFormat.QS && t.StartsWith("TxnResp", StringComparison.Ordinal) &&
                f.TryGetValue("EcrRefNo", out got) && got.Trim().Length > 0 && got.Trim() != pending.Req.Ref)
            {
                Log("!!", "STALE result for ECR ref " + got.Trim() + " (waiting for " + pending.Req.Ref + ") - ignored: " +
                          Get(f, "Txn") + " " + Get(f, "Amount") + " " + Get(f, "Currency") + " RespCode " + Get(f, "RespCode") + " RRN " + Get(f, "RRN"));
                Display(null, "STALE result for ref " + got.Trim() + " ignored");
                return;
            }
            double secs = (DateTime.Now - t0).TotalSeconds;
            Log("==", string.Format(CultureInfo.InvariantCulture, "RESULT after {0:N1} s: {1}", secs, t.Split('\r', '\n')[0]));
            if (t.StartsWith("SettlementResp", StringComparison.Ordinal))
            {
                approved++;
                Result(string.Format(CultureInfo.InvariantCulture, "SETTLEMENT DONE  ({0:N1} s)", secs), FieldList(f), "ok");
                Display("SETTLEMENT DONE", "");
            }
            else if (t.StartsWith("TxnResp", StringComparison.Ordinal))
            {
                bool ok = Get(f, "RespCode") == "00";
                if (ok) approved++; else declined++;
                string body =
                    "Txn " + Get(f, "Txn") + "   Resp " + Get(f, "RespCode") + " " + Get(f, "RespMsg") + "\r\n" +
                    "Card " + Get(f, "Card") + "   Entry " + Get(f, "Entry") + "   " + Get(f, "Card Issuer") + "\r\n" +
                    "RRN " + Get(f, "RRN") + "   Auth " + Get(f, "Auth") + "\r\n" +
                    "Invoice " + Get(f, "Invoice") + "   Batch " + Get(f, "Batch") + "\r\n" +
                    "ECR ref " + Get(f, "EcrRefNo") + "   TID " + Get(f, "TID") + "   MID " + Get(f, "MID") + "\r\n" +
                    string.Format(CultureInfo.InvariantCulture, "Result in {0:N1} s", secs) + "\r\n\r\nAll fields:\r\n" + FieldList(f);
                Result((ok ? "APPROVED  " : "DECLINED  ") + Get(f, "Amount") + " " + Get(f, "Currency"), body, ok ? "ok" : "bad");
                Display(ok ? "APPROVED" : "DECLINED", Get(f, "RespMsg"));
                if (ok) Remember(pending.Req, Get(f, "Invoice"));
            }
            else
            {
                declined++;
                Result(t.ToUpperInvariant(), string.Format(CultureInfo.InvariantCulture, "The terminal ended the transaction with \"{0}\" after {1:N1} s.", t, secs), "bad");
                Display(t.ToUpperInvariant(), "");
            }
            EndTxn();
        }

        void OnKvMessage(string m)
        {
            var f = Fields(m, true);
            f.Remove("<START>");
            f.Remove("<END>");
            bool empty = Regex.Replace(m, @"[\s"",]|<START>|<END>", "").Length == 0;
            if (empty)
            {
                if (state == "handshake") HandshakeOk();
                return;
            }
            string mirror;
            if (f.TryGetValue("MirrorMsg", out mirror)) { Display(null, mirror); return; }
            if (!f.ContainsKey("STATUS")) { Display(null, Plain(m)); return; }
            if (state != "result" || pending == null)
            {
                Log("--", "(final message outside a transaction)");
                return;
            }
            double secs = (DateTime.Now - t0).TotalSeconds;
            string code = Get(f, "RESPONSECODE");
            bool ok = code == "00";
            if (ok) approved++; else declined++;
            string type = Get(f, "TXNTYPE");
            Log("==", string.Format(CultureInfo.InvariantCulture, "RESULT after {0:N1} s: STATUS={1} RESPONSECODE={2} TXNTYPE={3}", secs, Get(f, "STATUS"), code, type));
            string body =
                "Type " + type + "   Status " + Get(f, "STATUS") + "   Resp " + code + " " + Get(f, "RSPTEXT") + "\r\n" +
                "PAN " + Get(f, "PAN") + "   Entry " + Get(f, "ENTRYMODE") + "   " + Get(f, "CARDTYPE") + "\r\n" +
                "TXNID " + Get(f, "TXNID") + "   RRN " + Get(f, "REFERENCENO") + "   Auth " + Get(f, "APPCODE") + "\r\n" +
                "Batch " + Get(f, "BATCHNO") + "   TID " + Get(f, "TERMINALID") + "   MID " + Get(f, "MERCHANTID") + "\r\n" +
                string.Format(CultureInfo.InvariantCulture, "Result in {0:N1} s", secs) + "\r\n\r\nAll fields:\r\n" + FieldList(f);
            string title = type == "SETTLE" ? (ok ? "SETTLEMENT DONE" : "SETTLEMENT FAILED")
                : (ok ? "APPROVED  " : (code == "UC" ? "CANCELLED  " : "DECLINED  ")) + Get(f, "AMT");
            Result(title, body, ok ? "ok" : "bad");
            Display(ok ? "APPROVED" : "DECLINED", Get(f, "RSPTEXT"));
            if (ok && type != "SETTLE") Remember(pending.Req, Get(f, "TXNID"));
            // KV Jordan: the POS reverses an approved sale unless the register acks it with an empty frame within 1.5 s.
            if (cfg.Format == EcrFormat.KVJordan && ok && type == "SALE")
            {
                if (cfg.KvAck) ackAt = DateTime.Now.AddMilliseconds(Math.Max(0, cfg.KvAckDelayMs));
                else Log("!!", "delivery ack OFF: the POS should reverse this sale after 1.5 s");
            }
            EndTxn();
        }

        void SendKvAck()
        {
            if (sp == null) { Log("!!", "delivery ack not sent: the port is closed"); return; }
            Write(KvFrame(new string[0]), "delivery ack" + (cfg.KvAckDelayMs > 0 ? " after " + cfg.KvAckDelayMs + " ms" : ""));
            ScheduleClose(0);
        }

        void Remember(TxnRequest r, string txnId)
        {
            if (r.Type == "Sale")
            {
                lastSale = r;
                lastSaleTxnId = txnId == "-" ? null : txnId;
                Log("--", "approved sale saved for Void: ref " + r.Ref + (lastSaleTxnId == null ? "" : ", TXNID/Invoice " + lastSaleTxnId));
            }
            else if (r.Type == "Preauth")
            {
                lastPreauth = r;
                Log("--", "approved preauth saved for Completion: ref " + r.Ref);
            }
            Changed();
        }

        static string FieldList(Dictionary<string, string> f)
        {
            var sb = new StringBuilder();
            foreach (var kv in f) sb.Append(kv.Key).Append(" = ").Append(kv.Value).Append("\r\n");
            return sb.ToString();
        }

        void SendFreeText(byte[] data, int repeat, int intervalMs)
        {
            if (!EnsureOpen()) return;
            string s = Latin1.GetString(data);
            for (int i = 0; i < Math.Max(1, repeat); i++)
            {
                if (sp == null) return;
                if (!Write(s, "free text" + (repeat > 1 ? " " + (i + 1) + "/" + repeat : ""))) return;
                if (i < repeat - 1)
                {
                    // Keep reading between repeats so replies show in order.
                    var until = DateTime.Now.AddMilliseconds(Math.Max(0, intervalMs));
                    while (DateTime.Now < until && sp != null)
                    {
                        foreach (var m in ReadMessages()) OnMessage(m);
                        Monitor.Exit(gate);
                        Thread.Sleep(20);
                        Monitor.Enter(gate);
                    }
                }
            }
            // Per-transaction mode: keep the port open a few seconds so the answer can be read.
            if (state == "idle") ScheduleClose(5000);
        }
    }
}
