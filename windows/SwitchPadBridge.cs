// SwitchPadBridge - reads an XInput pad (e.g. Parsec's virtual Xbox pad) and streams it to the
// SwitchPad Pi Zero W over Wi-Fi, which acts as a wired controller on the Switch.
// (Bluetooth was tried and dropped: on this hardware it kept disconnecting, and it wasn't
// worth the effort to make it reliable.)
// Build with build.cmd (uses the C# compiler that ships with Windows; nothing to install).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[StructLayout(LayoutKind.Sequential)]
struct XInputState
{
    public uint Packet;
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LX, LY, RX, RY;
    public uint Reserved; // XInputGetStateEx may write a little past XINPUT_STATE
}

static class XInput
{
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    static extern int GetStateEx(int user, out XInputState state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern int GetStateStd(int user, out XInputState state);

    static bool exAvailable = true;
    public static bool GuideSupported { get { return exAvailable; } }

    // GetStateEx is the undocumented variant that also reports the Guide button.
    public static bool TryGet(int user, out XInputState state)
    {
        if (exAvailable)
        {
            try { return GetStateEx(user, out state) == 0; }
            catch (EntryPointNotFoundException) { exAvailable = false; }
        }
        return GetStateStd(user, out state) == 0;
    }

    public const ushort UP = 0x0001, DOWN = 0x0002, LEFT = 0x0004, RIGHT = 0x0008,
        START = 0x0010, BACK = 0x0020, LS = 0x0040, RS = 0x0080, LB = 0x0100, RB = 0x0200,
        GUIDE = 0x0400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
}

static class Native
{
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);

    // With "Quick Edit" on, one click into the console window freezes all console output
    // until a key is pressed. Turn it off for this window.
    public static void DisableQuickEdit()
    {
        try
        {
            IntPtr h = GetStdHandle(-10); // STD_INPUT_HANDLE
            uint mode;
            if (GetConsoleMode(h, out mode)) SetConsoleMode(h, (mode & ~0x40u) | 0x80u); // -QUICK_EDIT, +EXTENDED_FLAGS
        }
        catch (Exception) { }
    }
}

// One clock for everything (milliseconds since start, sub-millisecond resolution).
static class Clock
{
    static readonly Stopwatch sw = Stopwatch.StartNew();
    public static double Ms { get { return sw.Elapsed.TotalMilliseconds; } }
}

// Console output runs on its own thread, so a slow or frozen console can never delay sending.
static class Con
{
    static readonly Queue<string> lines = new Queue<string>();
    static readonly AutoResetEvent wake = new AutoResetEvent(false);
    static volatile string status;
    static volatile bool started;
    static int pending;

    public static void Start()
    {
        var t = new Thread(Loop);
        t.IsBackground = true;
        t.Start();
        started = true;
    }

    public static void Line(string s)
    {
        if (!started) { Console.WriteLine(s); return; }
        lock (lines) { lines.Enqueue(s); pending++; }
        wake.Set();
    }

    public static void Status(string s) { status = s; }

    // Wait (briefly) until everything queued has been printed.
    public static void Flush()
    {
        for (int i = 0; i < 50; i++)
        {
            lock (lines) if (pending == 0) break;
            wake.Set();
            Thread.Sleep(10);
        }
    }

    static int Width()
    {
        try { return Math.Max(20, Console.BufferWidth - 1); } catch (Exception) { return 79; }
    }

    static void Loop()
    {
        string shown = null;
        while (true)
        {
            wake.WaitOne(200);
            int width = Width();
            while (true)
            {
                string l;
                lock (lines) { if (lines.Count == 0) break; l = lines.Dequeue(); }
                try
                {
                    Console.Write("\r" + new string(' ', width) + "\r");
                    Console.WriteLine(l);
                }
                catch (Exception) { }
                lock (lines) pending--;
                shown = null;
            }
            string st = status;
            if (st != null && st != shown)
            {
                if (st.Length > width) st = st.Substring(0, width);
                try { Console.Write("\r" + st.PadRight(width)); } catch (Exception) { }
                shown = status;
            }
        }
    }
}

// Round-trip time of the PC <-> Pi link, from the Pi echoing the newest packet it received.
class Rtt
{
    readonly double[] sentAt = new double[1024];
    readonly uint[] sentSeq = new uint[1024];
    readonly object gate = new object();
    double sum, max, smooth = -1;
    int count;

    public void Sent(uint seq)
    {
        lock (gate) { sentAt[seq & 1023] = Clock.Ms; sentSeq[seq & 1023] = seq; }
    }

    // echo = newest seq the Pi received, ageMs = how long before its status it arrived there.
    public void Echo(uint echo, int ageMs)
    {
        lock (gate)
        {
            if (sentSeq[echo & 1023] != echo || sentAt[echo & 1023] == 0) return;
            double rtt = Clock.Ms - sentAt[echo & 1023] - ageMs;
            if (rtt < 0 || rtt > 10000) return;
            sum += rtt; count++;
            if (rtt > max) max = rtt;
            smooth = smooth < 0 ? rtt : smooth * 0.8 + rtt * 0.2;
        }
    }

    // Recent value for the status line, or -1 if none yet.
    public double Smooth { get { lock (gate) return smooth; } }

    // "avg 6.1 max 14.0 ms (20)" for the stats line, then reset.
    public string TakeSummary()
    {
        lock (gate)
        {
            string s = count == 0 ? "no data" :
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "avg {0:0.0} max {1:0.0} ms ({2} samples)", sum / count, max, count);
            sum = max = 0; count = 0;
            return s;
        }
    }

    public void Reset() { lock (gate) smooth = -1; }
}

// Log file next to the exe (logs\bridge-<date>-<time>.log). Every event goes here and to the console.
static class Log
{
    static readonly object gate = new object();
    static System.IO.StreamWriter writer;
    public static string FilePath = "(not open)";

    public static void Open()
    {
        string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        try { System.IO.Directory.CreateDirectory(dir); }
        catch (Exception) { dir = System.IO.Path.GetTempPath(); }
        FilePath = System.IO.Path.Combine(dir, "bridge-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        try
        {
            writer = new System.IO.StreamWriter(FilePath, true, new UTF8Encoding(false));
            writer.AutoFlush = true;
        }
        catch (Exception e) { Console.WriteLine("Could not open log file " + FilePath + ": " + e.Message); writer = null; }
    }

    public static void Write(string msg) { Write(msg, true); }

    public static void Write(string msg, bool console)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "  " + msg;
        lock (gate)
        {
            if (writer != null) { try { writer.WriteLine(line); } catch (Exception) { } }
        }
        if (console) Con.Line(line);
    }

    public static void Close() { lock (gate) { if (writer != null) { writer.Close(); writer = null; } } }
}

class LinkStats { public int Sent, SendErrors, StatusRx; public readonly Rtt Rtt = new Rtt(); }

// Pi -> PC status: "SWS1" + attached(1) + version(1) + pad(2) [+ echo seq uint32 + age ms uint16 + pad(2)].
// Version 0 (older Pi software) is 8 bytes, version 1 is 16 bytes.
static class StatusMsg
{
    public static bool IsMagic(byte[] d, int i)
    {
        return d[i] == 'S' && d[i + 1] == 'W' && d[i + 2] == 'S' && d[i + 3] == '1';
    }

    public static int Length(byte[] d, int i) { return d[i + 5] == 1 ? 16 : 8; }

    // Reads one message at i (caller checked the length). Returns "Switch attached".
    public static bool Read(byte[] d, int i, LinkStats stats)
    {
        stats.StatusRx++;
        if (Length(d, i) == 16)
            stats.Rtt.Echo(BitConverter.ToUInt32(d, i + 8), BitConverter.ToUInt16(d, i + 12));
        return d[i + 4] == 1;
    }
}

// Switch report: buttons uint16 LE, hat, LX, LY, RX, RY, vendor (8 bytes). Must match switchpad.py.
static class Report
{
    public const int Y = 1 << 0, B = 1 << 1, A = 1 << 2, X = 1 << 3, L = 1 << 4, R = 1 << 5,
        ZL = 1 << 6, ZR = 1 << 7, MINUS = 1 << 8, PLUS = 1 << 9, LCLICK = 1 << 10,
        RCLICK = 1 << 11, HOME = 1 << 12, CAPTURE = 1 << 13;
    public const int HAT_CENTER = 8;
    static readonly string[] Names = { "Y", "B", "A", "X", "L", "R", "ZL", "ZR", "-", "+", "LS", "RS", "HOME", "CAPTURE" };
    static readonly string[] HatNames = { "Up", "Up-Right", "Right", "Down-Right", "Down", "Down-Left", "Left", "Up-Left" };

    public static byte[] Make(int buttons, int hat, byte lx, byte ly, byte rx, byte ry)
    {
        return new byte[] { (byte)(buttons & 0xFF), (byte)(buttons >> 8), (byte)hat, lx, ly, rx, ry, 0 };
    }

    public static byte[] Neutral() { return Make(0, HAT_CENTER, 128, 128, 128, 128); }

    public static bool Same(byte[] a, byte[] b)
    {
        if (a == null || b == null) return false;
        for (int i = 0; i < 8; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // Wire packet: "SWP1" + seq uint32 LE + 8-byte report.
    public static byte[] Packet(uint seq, byte[] report)
    {
        var pkt = new byte[16];
        pkt[0] = (byte)'S'; pkt[1] = (byte)'W'; pkt[2] = (byte)'P'; pkt[3] = (byte)'1';
        BitConverter.GetBytes(seq).CopyTo(pkt, 4); // little-endian on Windows
        Buffer.BlockCopy(report, 0, pkt, 8, 8);
        return pkt;
    }

    public static string Describe(byte[] r)
    {
        var sb = new StringBuilder();
        int buttons = r[0] | (r[1] << 8);
        for (int i = 0; i < Names.Length; i++) if ((buttons & (1 << i)) != 0) sb.Append(Names[i]).Append(' ');
        if (r[2] < 8) sb.Append("Dpad-").Append(HatNames[r[2]]).Append(' ');
        if (Math.Abs(r[3] - 128) > 20 || Math.Abs(r[4] - 128) > 20) sb.Append("LStick ");
        if (Math.Abs(r[5] - 128) > 20 || Math.Abs(r[6] - 128) > 20) sb.Append("RStick ");
        return sb.Length == 0 ? "-" : sb.ToString().TrimEnd();
    }
}

static class Mapper
{
    // positional = buttons keep their physical position (Xbox A, bottom -> Switch B, bottom).
    public static byte[] Build(XInputState s, bool positional)
    {
        ushort x = s.Buttons;
        bool back = (x & XInput.BACK) != 0, start = (x & XInput.START) != 0;
        bool guide = (x & XInput.GUIDE) != 0, rs = (x & XInput.RS) != 0;

        // Home = Guide, or Back+Start. Capture = Back + right-stick click.
        bool home = guide || (back && start);
        bool capture = back && rs;
        if (back && start) { back = false; start = false; }
        if (capture) { back = false; rs = false; }

        int b = 0;
        bool xa = (x & XInput.A) != 0, xb = (x & XInput.B) != 0, xx = (x & XInput.X) != 0, xy = (x & XInput.Y) != 0;
        if (positional)
        {
            if (xa) b |= Report.B;
            if (xb) b |= Report.A;
            if (xx) b |= Report.Y;
            if (xy) b |= Report.X;
        }
        else
        {
            if (xa) b |= Report.A;
            if (xb) b |= Report.B;
            if (xx) b |= Report.X;
            if (xy) b |= Report.Y;
        }
        if ((x & XInput.LB) != 0) b |= Report.L;
        if ((x & XInput.RB) != 0) b |= Report.R;
        if (s.LeftTrigger > 30) b |= Report.ZL;
        if (s.RightTrigger > 30) b |= Report.ZR;
        if (back) b |= Report.MINUS;
        if (start) b |= Report.PLUS;
        if ((x & XInput.LS) != 0) b |= Report.LCLICK;
        if (rs) b |= Report.RCLICK;
        if (home) b |= Report.HOME;
        if (capture) b |= Report.CAPTURE;

        int hat = Hat((x & XInput.UP) != 0, (x & XInput.DOWN) != 0, (x & XInput.LEFT) != 0, (x & XInput.RIGHT) != 0);
        return Report.Make(b, hat, Axis(s.LX), InvAxis(s.LY), Axis(s.RX), InvAxis(s.RY));
    }

    public static int Hat(bool up, bool down, bool left, bool right)
    {
        if (up && down) { up = false; down = false; }
        if (left && right) { left = false; right = false; }
        if (up) return right ? 1 : left ? 7 : 0;
        if (down) return right ? 3 : left ? 5 : 4;
        if (right) return 2;
        if (left) return 6;
        return Report.HAT_CENTER;
    }

    static byte Axis(short v) { return (byte)((v + 32768) >> 8); }
    static byte InvAxis(short v) { return (byte)(255 - ((v + 32768) >> 8)); } // XInput Y up = +, Switch Y down = +
}

// Scripted input for testing without a gamepad. Avoids A so it can't launch anything.
static class TestScript
{
    public static byte[] At(long ms)
    {
        long t = ms % 12000;
        if (t < 1000) return Report.Neutral();
        if (t < 5500) // L+R three times: registers the pad on "Change Grip/Order"
        {
            long p = (t - 1000) % 1500;
            return p < 500 ? Report.Make(Report.L | Report.R, Report.HAT_CENTER, 128, 128, 128, 128) : Report.Neutral();
        }
        if (t < 6000) return Report.Make(0, 2, 128, 128, 128, 128); // D-pad right
        if (t < 6500) return Report.Neutral();
        if (t < 7000) return Report.Make(0, 6, 128, 128, 128, 128); // D-pad left
        if (t < 8000) return Report.Neutral();
        if (t < 11000) // left stick circle
        {
            double a = (t - 8000) / 1000.0 * 2 * Math.PI;
            return Report.Make(0, Report.HAT_CENTER, (byte)(128 + 127 * Math.Cos(a)), (byte)(128 - 127 * Math.Sin(a)), 128, 128);
        }
        return Report.Neutral();
    }
}

// ------------------------------------------------------------------ Wi-Fi (UDP) link to the Pi

class UdpLink
{
    readonly UdpClient udp;
    readonly string host;
    readonly int port;
    volatile IPEndPoint target;
    uint seq;
    long lastStatusMs = -100000; // Clock.Ms when the Pi last answered
    bool attached;
    readonly LinkStats stats = new LinkStats();
    public LinkStats Stats { get { return stats; } }

    public UdpLink(string host, int port)
    {
        this.host = host;
        this.port = port;
        udp = new UdpClient(0, AddressFamily.InterNetwork);
        // Ignore ICMP "port unreachable" (otherwise Receive throws WSAECONNRESET while the Pi boots).
        const int SIO_UDP_CONNRESET = -1744830452;
        udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        IPAddress ip;
        if (IPAddress.TryParse(host, out ip)) target = new IPEndPoint(ip, port);
        else
        {
            // Looking up a name like switchpad.local can take 10+ s on Windows. Never do it in the
            // sending loop: look it up in the background and keep using the last address meanwhile.
            var t = new Thread(ResolveLoop);
            t.IsBackground = true;
            t.Start();
        }
    }

    double SinceStatusMs { get { return Clock.Ms - Interlocked.Read(ref lastStatusMs); } }
    public bool Online { get { return SinceStatusMs < 2000; } }
    public bool SwitchAttached { get { return attached; } }
    public string Status
    {
        get
        {
            var t = target;
            return "Wi-Fi " + (Online ? "online (" + t.Address + ")" : t == null ? "looking up " + host + "..." : "no reply from " + t.Address);
        }
    }

    void ResolveLoop()
    {
        bool first = true;
        while (true)
        {
            // Look up again only if the Pi has been silent for a while (its address may have changed).
            if (target == null || SinceStatusMs > 5000)
            {
                var t0 = Stopwatch.StartNew();
                try
                {
                    IPAddress ip = null;
                    foreach (var a in Dns.GetHostAddresses(host))
                        if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                    if (ip == null) Log.Write("Wi-Fi: " + host + " has no IPv4 address");
                    else if (target == null || !target.Address.Equals(ip))
                    {
                        Log.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Wi-Fi: {0} is {1} (lookup took {2:0.0} s{3})", host, ip,
                            t0.ElapsedMilliseconds / 1000.0,
                            first ? "; to skip the lookup next time, start with --pi " + ip : ""));
                        target = new IPEndPoint(ip, port);
                        first = false;
                    }
                }
                catch (SocketException e)
                {
                    Log.Write("Wi-Fi: can't look up " + host + " (" + e.SocketErrorCode + ", after " +
                              t0.ElapsedMilliseconds + " ms), retrying");
                }
                Thread.Sleep(5000);
            }
            else Thread.Sleep(250);
        }
    }

    public void Send(byte[] report)
    {
        var t = target;
        if (t == null) return;
        uint s = seq++;
        var pkt = Report.Packet(s, report);
        try { stats.Rtt.Sent(s); udp.Send(pkt, pkt.Length, t); stats.Sent++; }
        catch (SocketException e)
        {
            if (stats.SendErrors++ == 0) Log.Write("Wi-Fi: send failed (" + e.SocketErrorCode + ")");
        }
    }

    public void Poll()
    {
        while (udp.Available > 0)
        {
            IPEndPoint from = null;
            byte[] d;
            try { d = udp.Receive(ref from); } catch (SocketException) { return; }
            if (d.Length >= 8 && StatusMsg.IsMagic(d, 0) && d.Length >= StatusMsg.Length(d, 0))
            {
                Interlocked.Exchange(ref lastStatusMs, (long)Clock.Ms);
                attached = StatusMsg.Read(d, 0, stats);
            }
        }
    }
}

// ------------------------------------------------------------------ main

static class Program
{
    static volatile bool stop;

    const string Usage =
@"SwitchPadBridge - send an Xbox/XInput pad to the SwitchPad Pi (Nintendo Switch).

Usage: SwitchPadBridge.exe [options]
  --pi HOST       the Pi's IP address or name (default: switchpad.local).
                  The IP address is faster, e.g. --pi 192.168.137.79
  --pad N         XInput slot 0-3 to read (default: auto = highest connected slot)
  --layout L      position (default): Xbox A/B/X/Y -> Switch B/A/Y/X (same physical spot)
                  label: Xbox A/B/X/Y -> Switch A/B/X/Y (same letter)
  --interval MS   minimum time between packets (default: 1)
  --test          send a scripted test pattern instead of reading a pad
  --list          list XInput pads, then exit
  --seconds N     stop after N seconds (default: run until Ctrl+C)

Extra buttons: Guide or Back+Start = HOME, Back + right-stick click = CAPTURE.";

    static int Main(string[] args)
    {
        string pi = "switchpad.local";
        int pad = -1, seconds = 0, interval = 1;
        bool positional = true, test = false, list = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--pi": case "--wifi": pi = args[++i]; break; // --wifi: older name
                    case "--pad": pad = int.Parse(args[++i]); if (pad < 0 || pad > 3) throw new FormatException(); break;
                    case "--layout":
                        string l = args[++i].ToLowerInvariant();
                        if (l == "position") positional = true;
                        else if (l == "label") positional = false;
                        else throw new FormatException();
                        break;
                    case "--interval": interval = int.Parse(args[++i]); break;
                    case "--test": test = true; break;
                    case "--list": list = true; break;
                    case "--seconds": seconds = int.Parse(args[++i]); break;
                    default: throw new FormatException();
                }
            }
        }
        catch (Exception)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        if (list) { foreach (var l in DeviceLines()) Console.WriteLine(l); return 0; }

        Native.DisableQuickEdit();
        Con.Start();
        Log.Open();
        Log.Write("=== SwitchPadBridge " + Version + " started, log file: " + Log.FilePath);
        Log.Write("Arguments: " + (args.Length == 0 ? "(none)" : string.Join(" ", args)) +
                  " | Windows build " + WindowsBuild() + (Environment.Is64BitProcess ? " 64-bit" : " 32-bit"), false);
        foreach (var l in DeviceLines()) Log.Write(l, false);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("CRASH (unhandled): " + e.ExceptionObject);

        var link = new UdpLink(pi, 5005);

        Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop = true; };
        Native.timeBeginPeriod(1);
        try { Run(link, "the Pi at " + pi, pad, positional, test, seconds, interval); }
        catch (Exception e) { Log.Write("CRASH: " + e); throw; }
        finally
        {
            Native.timeEndPeriod(1);
            Log.Write("=== Stopped. Log file: " + Log.FilePath);
            Log.Close();
            Con.Flush();
            Console.WriteLine();
        }
        return 0;
    }

    const string Version = "2026-11-20c (Wi-Fi only)";

    static string WindowsBuild()
    {
        try
        {
            const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            return Microsoft.Win32.Registry.GetValue(key, "ProductName", "?") + " " +
                   Microsoft.Win32.Registry.GetValue(key, "CurrentBuild", "?");
        }
        catch (Exception) { return Environment.OSVersion.Version.ToString(); }
    }

    static System.Collections.Generic.List<string> DeviceLines()
    {
        var lines = new System.Collections.Generic.List<string>();
        lines.Add("XInput pads:");
        bool any = false;
        for (int i = 0; i < 4; i++)
        {
            XInputState s;
            if (XInput.TryGet(i, out s))
            {
                any = true;
                lines.Add(string.Format("  Slot {0}: connected (buttons 0x{1:X4}, LX {2}, LY {3})", i, s.Buttons, s.LX, s.LY));
            }
            else lines.Add(string.Format("  Slot {0}: -", i));
        }
        if (!any) lines.Add("  No XInput pads. Is a Parsec guest connected with a controller?");
        lines.Add(XInput.GuideSupported ? "  Guide button: supported" : "  Guide button: not readable (use Back+Start for HOME)");
        return lines;
    }

    static string SlotList()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 4; i++)
        {
            XInputState s;
            if (XInput.TryGet(i, out s)) sb.Append(sb.Length == 0 ? "" : ",").Append(i);
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    static string ButtonsOnly(byte[] r)
    {
        var copy = (byte[])r.Clone();
        copy[3] = copy[4] = copy[5] = copy[6] = 128; // ignore sticks
        return Report.Describe(copy);
    }

    static int PickPad(int fixedPad)
    {
        XInputState s;
        if (fixedPad >= 0) return XInput.TryGet(fixedPad, out s) ? fixedPad : -1;
        for (int i = 3; i >= 0; i--) if (XInput.TryGet(i, out s)) return i; // Parsec's pads usually take the later slots
        return -1;
    }

    static void Run(UdpLink link, string linkName, int fixedPad, bool positional, bool test, int seconds, int interval)
    {
        var clock = Stopwatch.StartNew();
        byte[] last = null;
        long lastSend = -1000, lastPrint = -1000, lastScan = -1000, lastSlotScan = -1000, lastStats = 0, prevIter = 0;
        int pad = -1;
        string slots = null, lastButtons = null;
        bool wasOnline = false, wasAttached = false;
        // stats, reset every 10 s
        int changes = 0, slowSends = 0;
        long maxSendMs = 0, maxLoopMs = 0;
        var sendTimer = new Stopwatch();

        Log.Write(string.Format("Sending to {0} ({1}, {2}, min interval {3} ms). Ctrl+C to quit.", linkName,
            test ? "TEST PATTERN" : "layout " + (positional ? "position" : "label"),
            fixedPad >= 0 ? "pad " + fixedPad : "pad auto", interval));

        while (!stop && (seconds <= 0 || clock.ElapsedMilliseconds < seconds * 1000L))
        {
            long now = clock.ElapsedMilliseconds;
            maxLoopMs = Math.Max(maxLoopMs, now - prevIter);
            prevIter = now;

            // Which XInput slots exist (Parsec adds/removes its virtual pads as guests come and go).
            if (now - lastSlotScan >= 1000)
            {
                lastSlotScan = now;
                string sl = SlotList();
                if (sl != slots) { Log.Write("XInput pads connected in slots: " + sl); slots = sl; }
            }

            byte[] report;
            if (test)
            {
                report = TestScript.At(now);
            }
            else
            {
                XInputState st = new XInputState();
                bool have = pad >= 0 && XInput.TryGet(pad, out st);
                if (!have && now - lastScan >= 1000)
                {
                    lastScan = now;
                    int p = PickPad(fixedPad);
                    if (p != pad)
                    {
                        Log.Write(p >= 0 ? "Using XInput pad in slot " + p
                                         : (pad >= 0 ? "Pad in slot " + pad + " disappeared. " : "") + "Waiting for a controller...");
                        pad = p;
                    }
                    if (pad >= 0) have = XInput.TryGet(pad, out st);
                }
                report = have ? Mapper.Build(st, positional) : Report.Neutral();
            }

            // Every button / D-pad change goes into the log file (sticks are summarized in stats).
            string buttons = ButtonsOnly(report);
            if (buttons != lastButtons) { Log.Write("input: " + buttons, false); lastButtons = buttons; }

            // Send on change (at most every `interval` ms, always the newest state),
            // plus a 20 Hz heartbeat so the Pi's failsafe stays happy.
            long since = now - lastSend;
            if ((!Report.Same(report, last) && since >= interval) || since >= 50)
            {
                if (!Report.Same(report, last)) changes++;
                sendTimer.Restart();
                link.Send(report);
                long ms = sendTimer.ElapsedMilliseconds;
                maxSendMs = Math.Max(maxSendMs, ms);
                if (ms >= 20) slowSends++;
                last = report;
                lastSend = now;
            }
            link.Poll();

            if (link.Online != wasOnline)
            {
                wasOnline = link.Online;
                Log.Write(wasOnline ? "Pi is answering (" + link.Status + ")" : "Pi is NOT answering any more (" + link.Status + ")");
            }
            if (wasOnline && link.SwitchAttached != wasAttached)
            {
                wasAttached = link.SwitchAttached;
                Log.Write(wasAttached ? "Pi reports: Switch/PC is using the controller (USB configured)"
                                      : "Pi reports: Switch NOT connected on USB");
            }

            if (now - lastStats >= 10000)
            {
                var s = link.Stats;
                Log.Write(string.Format(
                    "stats {0:0}s: {1} | Switch: {2} | pad slot {3} (slots {4}) | sent {5}, send errors {6}, " +
                    "changes {7} | status msgs from Pi {8} | round trip PC-Pi-PC {9} | max send {10} ms, slow sends {11} | " +
                    "max loop {12} ms | holding: {13}",
                    (now - lastStats) / 1000.0, link.Status,
                    link.Online ? (link.SwitchAttached ? "connected" : "not connected") : "?",
                    pad, slots, s.Sent, s.SendErrors, changes, s.StatusRx,
                    s.Rtt.TakeSummary(), maxSendMs, slowSends, maxLoopMs, Report.Describe(report)), false);
                s.Sent = s.SendErrors = s.StatusRx = 0;
                changes = slowSends = 0;
                maxSendMs = maxLoopMs = 0;
                lastStats = now;
            }

            if (now - lastPrint >= 200)
            {
                lastPrint = now;
                double rtt = link.Stats.Rtt.Smooth;
                Con.Status(string.Format("{0} | Switch: {1} | Ping: {2} | Pressed: {3}",
                    link.Status,
                    link.Online ? (link.SwitchAttached ? "connected" : "not connected") : "?",
                    link.Online && rtt >= 0 ? string.Format("{0:0} ms", rtt) : "?",
                    Report.Describe(report)));
            }
            Thread.Sleep(1);
        }

        for (int i = 0; i < 5; i++) { link.Send(Report.Neutral()); Thread.Sleep(10); } // release everything
    }
}
