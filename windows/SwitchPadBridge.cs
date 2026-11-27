// SwitchPad bridge: reads an XInput pad (e.g. Parsec's virtual Xbox pad) and the keyboard, and
// streams them over the network to the SwitchPad Pi, which acts as a wired controller on the Switch.
// (Bluetooth was tried and dropped: on this hardware it kept disconnecting, and it wasn't
// worth the effort to make it reliable.)
//
// build.cmd makes two programs from this file and SwitchPadGui.cs, with the C# compiler that
// ships with Windows (nothing to install):
//   SwitchPad.exe         window + tray icon: live status, keyboard mapping, settings
//   SwitchPadBridge.exe   console version of the same engine (collect-logs uses its --list)
// Both keep their settings in settings.ini and their logs in logs\ next to the exe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
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
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);
    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    // Windows 11 stops honouring timeBeginPeriod(1) for a program whose windows are minimized or
    // covered (SwitchPad in the tray!), and may run it in "efficiency mode". Both would add up to
    // ~16 ms of input lag. Opt out of both.
    public static string KeepTimerPrecise()
    {
        try
        {
            // ControlMask: EXECUTION_SPEED (1) | IGNORE_TIMER_RESOLUTION (4); StateMask 0 = never throttle them.
            var s = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
            return SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref s, (uint)Marshal.SizeOf(s))
                ? "ok" : "not available (error " + Marshal.GetLastWin32Error() + ")";
        }
        catch (EntryPointNotFoundException) { return "not available"; }
    }

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
    double sum, max, smooth = -1, secSum;
    int count, secCount;

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
            secSum += rtt; secCount++;
            if (rtt > max) max = rtt;
            smooth = smooth < 0 ? rtt : smooth * 0.8 + rtt * 0.2;
        }
    }

    // Recent value for the status line, or -1 if none yet.
    public double Smooth { get { lock (gate) return smooth; } }

    // Average since the last call (for the once-a-second ping graph), or -1 if no samples.
    public double TakeSecond()
    {
        lock (gate)
        {
            double v = secCount == 0 ? -1 : secSum / secCount;
            secSum = 0; secCount = 0;
            return v;
        }
    }

    // "avg 6.1 max 14.0 ms (20)" for the stats line, then reset.
    public string TakeSummary()
    {
        lock (gate)
        {
            string s = count == 0 ? "no data" :
                string.Format(CultureInfo.InvariantCulture,
                    "avg {0:0.0} max {1:0.0} ms ({2} samples)", sum / count, max, count);
            sum = max = 0; count = 0;
            return s;
        }
    }

    public void Reset() { lock (gate) { smooth = -1; secSum = 0; secCount = 0; } }
}

// Log file next to the exe (logs\bridge-<date>-<time>.log). Every event goes here, to the console
// (console version) and to the last lines kept in memory for the Log tab (window version).
static class Log
{
    static readonly object gate = new object();
    static StreamWriter writer;
    public static string FilePath = "(not open)";
    public static bool ToConsole = true;
    static readonly List<string> recent = new List<string>();
    static long total; // lines written since start

    public static string Dir { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"); } }

    public static void Open()
    {
        string dir = Dir;
        try { Directory.CreateDirectory(dir); }
        catch (Exception) { dir = Path.GetTempPath(); }
        FilePath = Path.Combine(dir, "bridge-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        try
        {
            writer = new StreamWriter(FilePath, true, new UTF8Encoding(false));
            writer.AutoFlush = true;
        }
        catch (Exception e) { Write("Could not open log file " + FilePath + ": " + e.Message); writer = null; }
    }

    public static void Write(string msg) { Write(msg, true); }

    public static void Write(string msg, bool console)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg;
        lock (gate)
        {
            if (writer != null) { try { writer.WriteLine(line); } catch (Exception) { } }
            recent.Add(line);
            total++;
            if (recent.Count > 2000) recent.RemoveRange(0, 500);
        }
        if (console && ToConsole) Con.Line(line);
    }

    // Lines written after `seen` (a count from an earlier call); updates `seen`.
    public static List<string> Since(ref long seen)
    {
        lock (gate)
        {
            long first = total - recent.Count;
            if (seen < first) seen = first;
            int start = (int)(seen - first);
            var result = recent.GetRange(start, recent.Count - start);
            seen = total;
            return result;
        }
    }

    public static void Close() { lock (gate) { if (writer != null) { writer.Close(); writer = null; } } }
}

class LinkStats
{
    public int Sent, SendErrors, StatusRx;    // reset with every stats line
    public long TotalSent, TotalStatusRx;     // never reset (per-second rates)
    public readonly Rtt Rtt = new Rtt();
}

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
        stats.TotalStatusRx++;
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
    // PS3 mode: the Pi sends the same bits as a PS3 pad (Capture has no PS3 button).
    static readonly string[] Ps3Names = { "Square", "Cross", "Circle", "Triangle", "L1", "R1", "L2", "R2", "Select", "Start", "L3", "R3", "PS", "(Capture)" };
    static readonly string[] HatNames = { "Up", "Up-Right", "Right", "Down-Right", "Down", "Down-Left", "Left", "Up-Left" };

    public static byte[] Make(int buttons, int hat, byte lx, byte ly, byte rx, byte ry)
    {
        return new byte[] { (byte)(buttons & 0xFF), (byte)(buttons >> 8), (byte)hat, lx, ly, rx, ry, 0 };
    }

    public static byte[] Neutral() { return Make(0, HAT_CENTER, 128, 128, 128, 128); }

    public static byte[] FromFrame(Frame f)
    {
        return Make(f.Buttons, Mapper.Hat(f.Up, f.Down, f.Left, f.Right), f.LX, f.LY, f.RX, f.RY);
    }

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

    public static string Describe(byte[] r, bool ps3)
    {
        var sb = new StringBuilder();
        int buttons = r[0] | (r[1] << 8);
        string[] names = ps3 ? Ps3Names : Names;
        for (int i = 0; i < names.Length; i++) if ((buttons & (1 << i)) != 0) sb.Append(names[i]).Append(' ');
        if (r[2] < 8) sb.Append("Dpad-").Append(HatNames[r[2]]).Append(' ');
        if (Math.Abs(r[3] - 128) > 20 || Math.Abs(r[4] - 128) > 20) sb.Append("LStick ");
        if (Math.Abs(r[5] - 128) > 20 || Math.Abs(r[6] - 128) > 20) sb.Append("RStick ");
        return sb.Length == 0 ? "-" : sb.ToString().TrimEnd();
    }
}

// What the Switch should see right now, before it's packed into a report. The controller and
// the keyboard both add to it.
class Frame
{
    public int Buttons;
    public bool Up, Down, Left, Right;
    public byte LX = 128, LY = 128, RX = 128, RY = 128; // Switch convention: 0 = left/up, 255 = right/down

    public void Clear()
    {
        Buttons = 0;
        Up = Down = Left = Right = false;
        LX = LY = RX = RY = 128;
    }
}

static class Mapper
{
    // positional = buttons keep their physical position (Xbox A, bottom -> Switch B, bottom).
    public static void FromXInput(XInputState s, Settings cfg, Frame f)
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
        if (cfg.Positional || cfg.Ps3) // PS3 buttons have no letters: always by position (Xbox A -> Cross)
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
        if (s.LeftTrigger > cfg.TriggerThreshold) b |= Report.ZL;
        if (s.RightTrigger > cfg.TriggerThreshold) b |= Report.ZR;
        if (back) b |= Report.MINUS;
        if (start) b |= Report.PLUS;
        if ((x & XInput.LS) != 0) b |= Report.LCLICK;
        if (rs) b |= Report.RCLICK;
        if (home) b |= Report.HOME;
        if (capture) b |= Report.CAPTURE;
        f.Buttons |= b;

        f.Up |= (x & XInput.UP) != 0;
        f.Down |= (x & XInput.DOWN) != 0;
        f.Left |= (x & XInput.LEFT) != 0;
        f.Right |= (x & XInput.RIGHT) != 0;
        Stick(s.LX, s.LY, cfg.Deadzone, out f.LX, out f.LY);
        Stick(s.RX, s.RY, cfg.Deadzone, out f.RX, out f.RY);
    }

    // XInput: Y up = +. Switch: Y down = +. deadzone in percent of full travel (0 = off).
    static void Stick(short x, short y, int deadzone, out byte bx, out byte by)
    {
        if (deadzone <= 0)
        {
            bx = (byte)((x + 32768) >> 8);
            by = (byte)(255 - ((y + 32768) >> 8));
            return;
        }
        double fx = Math.Max(-1.0, x / 32767.0), fy = Math.Max(-1.0, y / 32767.0);
        double mag = Math.Sqrt(fx * fx + fy * fy), dz = deadzone / 100.0;
        if (mag <= dz) { bx = by = 128; return; }
        double scale = Math.Min(1.0, (mag - dz) / (1 - dz)) / mag;
        bx = ToByte(fx * scale);
        by = ToByte(-fy * scale);
    }

    // -1..1 -> 1..255 (128 = centre)
    public static byte ToByte(double v)
    {
        int b = (int)Math.Round(128 + v * 127);
        return (byte)Math.Max(0, Math.Min(255, b));
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

// ------------------------------------------------------------------ keyboard

// Everything a key can be mapped to.
class Target
{
    public const int BUTTON = 0, DPAD = 1, LSTICK = 2, RSTICK = 3, WALK = 4;
    public readonly string Id, Label, Ps3Label;
    public readonly int Kind, Value; // Value: button bit, or direction 0 up, 1 down, 2 left, 3 right

    public Target(string id, string label, int kind, int value, string ps3Label = null)
    {
        Id = id; Label = label; Kind = kind; Value = value; Ps3Label = ps3Label ?? label;
    }

    public string LabelFor(bool ps3) { return ps3 ? Ps3Label : Label; }
}

static class Targets
{
    public static readonly Target[] All =
    {
        // The PS3 label is the button in the same spot (the Pi sends Switch A as Circle etc.).
        new Target("A", "A", Target.BUTTON, Report.A, "Circle"),
        new Target("B", "B", Target.BUTTON, Report.B, "Cross"),
        new Target("X", "X", Target.BUTTON, Report.X, "Triangle"),
        new Target("Y", "Y", Target.BUTTON, Report.Y, "Square"),
        new Target("L", "L", Target.BUTTON, Report.L, "L1"),
        new Target("R", "R", Target.BUTTON, Report.R, "R1"),
        new Target("ZL", "ZL", Target.BUTTON, Report.ZL, "L2"),
        new Target("ZR", "ZR", Target.BUTTON, Report.ZR, "R2"),
        new Target("Plus", "+ (Plus)", Target.BUTTON, Report.PLUS, "Start"),
        new Target("Minus", "- (Minus)", Target.BUTTON, Report.MINUS, "Select"),
        new Target("Home", "Home", Target.BUTTON, Report.HOME, "PS"),
        new Target("Capture", "Capture", Target.BUTTON, Report.CAPTURE, "Capture (no PS3 button)"),
        new Target("LS", "Left stick click", Target.BUTTON, Report.LCLICK, "L3 (left stick click)"),
        new Target("RS", "Right stick click", Target.BUTTON, Report.RCLICK, "R3 (right stick click)"),
        new Target("DUp", "D-pad up", Target.DPAD, 0),
        new Target("DDown", "D-pad down", Target.DPAD, 1),
        new Target("DLeft", "D-pad left", Target.DPAD, 2),
        new Target("DRight", "D-pad right", Target.DPAD, 3),
        new Target("LUp", "Left stick up", Target.LSTICK, 0),
        new Target("LDown", "Left stick down", Target.LSTICK, 1),
        new Target("LLeft", "Left stick left", Target.LSTICK, 2),
        new Target("LRight", "Left stick right", Target.LSTICK, 3),
        new Target("RUp", "Right stick up", Target.RSTICK, 0),
        new Target("RDown", "Right stick down", Target.RSTICK, 1),
        new Target("RLeft", "Right stick left", Target.RSTICK, 2),
        new Target("RRight", "Right stick right", Target.RSTICK, 3),
        new Target("Walk", "Walk (sticks tilt less while held)", Target.WALK, 0),
    };

    public static Target Find(string id)
    {
        foreach (var t in All) if (t.Id == id) return t;
        return null;
    }

    public static Dictionary<string, int[]> EmptyMap()
    {
        var m = new Dictionary<string, int[]>();
        foreach (var t in All) m[t.Id] = new int[2];
        return m;
    }

    // Windows virtual-key codes.
    public static Dictionary<string, int[]> DefaultMap()
    {
        var m = EmptyMap();
        m["LUp"][0] = 0x57; m["LDown"][0] = 0x53; m["LLeft"][0] = 0x41; m["LRight"][0] = 0x44; // W S A D
        m["RUp"][0] = 0x26; m["RDown"][0] = 0x28; m["RLeft"][0] = 0x25; m["RRight"][0] = 0x27; // arrow keys
        m["B"][0] = 0x4A; m["A"][0] = 0x4B; m["Y"][0] = 0x55; m["X"][0] = 0x49;                 // J K U I
        m["L"][0] = 0x51; m["R"][0] = 0x45; m["ZL"][0] = 0x31; m["ZR"][0] = 0x33;               // Q E 1 3
        m["Plus"][0] = 0x0D; m["Minus"][0] = 0x08;                                              // Enter, Backspace
        m["Home"][0] = 0x48; m["Capture"][0] = 0x7B;                                            // H, F12
        m["LS"][0] = 0x43; m["RS"][0] = 0x4F;                                                   // C, O
        m["DUp"][0] = 0x68; m["DDown"][0] = 0x62; m["DLeft"][0] = 0x64; m["DRight"][0] = 0x66; // numpad 8 2 4 6
        m["Walk"][0] = 0xA0;                                                                    // left Shift
        return m;
    }
}

static class KeyNames
{
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    // Name stored in settings.ini (the Windows Forms key name, e.g. "W", "D1", "LShiftKey").
    public static string Name(int vk)
    {
        var k = (System.Windows.Forms.Keys)vk;
        return Enum.IsDefined(typeof(System.Windows.Forms.Keys), k) ? k.ToString() : "VK" + vk.ToString("X2");
    }

    public static int Parse(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        int vk;
        if (s.StartsWith("VK", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk) && vk > 0 && vk < 256)
            return vk;
        System.Windows.Forms.Keys k;
        if (Enum.TryParse(s, true, out k) && ((int)k & ~0xFF) == 0 && (int)k != 0) return (int)k;
        throw new FormatException("unknown key '" + s + "'");
    }

    // Name for people, e.g. "Left Shift", "Num 8", "." (the character on this keyboard layout).
    public static string Pretty(int vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
        switch (vk)
        {
            case 0x08: return "Backspace";
            case 0x09: return "Tab";
            case 0x0D: return "Enter";
            case 0x10: return "Shift";
            case 0x11: return "Ctrl";
            case 0x12: return "Alt";
            case 0x13: return "Pause";
            case 0x14: return "Caps Lock";
            case 0x1B: return "Esc";
            case 0x20: return "Space";
            case 0x21: return "Page Up";
            case 0x22: return "Page Down";
            case 0x23: return "End";
            case 0x24: return "Home key";
            case 0x25: return "Left";
            case 0x26: return "Up";
            case 0x27: return "Right";
            case 0x28: return "Down";
            case 0x2C: return "Print Screen";
            case 0x2D: return "Insert";
            case 0x2E: return "Delete";
            case 0x5B: return "Left Win";
            case 0x5C: return "Right Win";
            case 0x5D: return "Menu";
            case 0x6A: return "Num *";
            case 0x6B: return "Num +";
            case 0x6D: return "Num -";
            case 0x6E: return "Num .";
            case 0x6F: return "Num /";
            case 0x90: return "Num Lock";
            case 0x91: return "Scroll Lock";
            case 0xA0: return "Left Shift";
            case 0xA1: return "Right Shift";
            case 0xA2: return "Left Ctrl";
            case 0xA3: return "Right Ctrl";
            case 0xA4: return "Left Alt";
            case 0xA5: return "Right Alt";
        }
        if (vk >= 0xBA && vk <= 0xE2) // punctuation: depends on the keyboard layout
        {
            try
            {
                uint c = MapVirtualKey((uint)vk, 2) & 0x7FFF; // MAPVK_VK_TO_CHAR
                if (c > ' ') return ((char)c).ToString();
            }
            catch (Exception) { }
        }
        return Name(vk);
    }

    // Very short name for the controller picture.
    public static string Short(int vk)
    {
        switch (vk)
        {
            case 0x25: return "←";
            case 0x26: return "↑";
            case 0x27: return "→";
            case 0x28: return "↓";
            case 0x08: return "Bksp";
            case 0x0D: return "Enter";
            case 0x14: return "Caps";
            case 0x20: return "Space";
            case 0x21: return "PgUp";
            case 0x22: return "PgDn";
            case 0x2D: return "Ins";
            case 0x2E: return "Del";
            case 0xA0: return "LShift";
            case 0xA1: return "RShift";
            case 0xA2: return "LCtrl";
            case 0xA3: return "RCtrl";
            case 0xA4: return "LAlt";
            case 0xA5: return "RAlt";
        }
        if (vk >= 0x60 && vk <= 0x69) return "N" + (vk - 0x60);
        return Pretty(vk);
    }

    public static string ToText(int[] keys)
    {
        var parts = new List<string>();
        foreach (int k in keys) if (k != 0) parts.Add(Name(k));
        return string.Join(", ", parts.ToArray());
    }

    public static string ListPretty(int[] keys)
    {
        var parts = new List<string>();
        foreach (int k in keys) if (k != 0) parts.Add(Pretty(k));
        return string.Join(", ", parts.ToArray());
    }
}

// Reads the keyboard system-wide with a low-level keyboard hook, so keys from a Parsec guest work
// whichever window has focus on this PC. The hook runs on its own thread with its own message
// loop: Windows silently removes a hook whose thread is too slow to answer.
static class KeyboardHook
{
    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    class Config { public bool Enabled, Block, RemoteOnly; public readonly bool[] Mapped = new bool[256]; }

    static readonly bool[] down = new bool[256];
    static readonly bool[] blocked = new bool[256];   // we swallowed this key's press: swallow its release too
    static readonly bool[] swallowUp = new bool[256]; // the press was used to assign a key
    static volatile Config cfg = new Config();
    static volatile Action<int> capture;
    static HookProc proc; // kept referenced so the garbage collector can't free it
    static IntPtr hook;
    static long lastKeyMs = -100000;

    public static volatile bool Running;
#pragma warning disable 649 // set by the window program only
    public static volatile bool Paused;        // the window's text box has focus: keys are typing, not playing
#pragma warning restore 649
    public static volatile int LastVk;
    public static volatile bool LastInjected;  // sent by a program (e.g. Parsec), not typed on this PC
    public static double LastKeyMs { get { return Interlocked.Read(ref lastKeyMs); } }

    public static void Start()
    {
        if (Running) return;
        var t = new Thread(() =>
        {
            proc = Proc;
            hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                Log.Write("Keyboard: could not install the keyboard hook (error " + Marshal.GetLastWin32Error() + "); keys won't work");
                return;
            }
            Running = true;
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
        });
        t.IsBackground = true;
        t.Name = "keyboard hook";
        t.Priority = ThreadPriority.AboveNormal;
        t.Start();
    }

    public static void SetConfig(Settings s)
    {
        var c = new Config { Enabled = s.Keyboard, Block = s.BlockKeys, RemoteOnly = s.RemoteKeysOnly };
        foreach (var kv in s.KeyMap)
            foreach (int vk in kv.Value)
                if (vk > 0 && vk < 256) c.Mapped[vk] = true;
        cfg = c;
        ReleaseAll();
    }

    public static bool IsDown(int vk) { return vk > 0 && vk < 256 && down[vk]; }

    // Forget held keys (settings changed, screen locked): a key held now has to be pressed again.
    public static void ReleaseAll() { for (int i = 0; i < 256; i++) down[i] = false; }

    // The next key pressed anywhere goes to `onKey` (and nowhere else) instead of the Switch.
    public static void BeginCapture(Action<int> onKey) { capture = onKey; }
    public static void CancelCapture() { capture = null; }

    static IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int vk = (int)(k.vkCode & 0xFF);
                int msg = wParam.ToInt32();
                bool isDown = msg == 0x100 || msg == 0x104; // WM_KEYDOWN, WM_SYSKEYDOWN
                bool injected = (k.flags & 0x10) != 0;      // LLKHF_INJECTED
                if (isDown)
                {
                    LastVk = vk;
                    LastInjected = injected;
                    Interlocked.Exchange(ref lastKeyMs, (long)Clock.Ms);
                }

                var cap = capture;
                if (isDown && cap != null)
                {
                    capture = null;
                    swallowUp[vk] = true;
                    down[vk] = false;
                    cap(vk);
                    return (IntPtr)1;
                }
                if (!isDown && swallowUp[vk]) { swallowUp[vk] = false; return (IntPtr)1; }

                var c = cfg;
                bool use = c.Enabled && !Paused && (!c.RemoteOnly || injected);
                down[vk] = isDown && use;
                if (isDown) blocked[vk] = use && c.Block && c.Mapped[vk];
                if (blocked[vk])
                {
                    if (!isDown) blocked[vk] = false;
                    return (IntPtr)1;
                }
            }
            catch (Exception) { } // never let an exception escape into Windows
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }
}

// Adds the held keys to a frame. Returns true if any mapped key is held.
static class KeyMapper
{
    public static bool Apply(Frame f, Settings cfg)
    {
        bool any = false, walk = false;
        int lx = 0, ly = 0, rx = 0, ry = 0; // y: up = +
        foreach (var t in Targets.All)
        {
            int[] keys;
            if (!cfg.KeyMap.TryGetValue(t.Id, out keys)) continue;
            bool held = false;
            foreach (int vk in keys) if (KeyboardHook.IsDown(vk)) held = true;
            if (!held) continue;
            any = true;
            switch (t.Kind)
            {
                case Target.BUTTON: f.Buttons |= t.Value; break;
                case Target.DPAD:
                    if (t.Value == 0) f.Up = true;
                    else if (t.Value == 1) f.Down = true;
                    else if (t.Value == 2) f.Left = true;
                    else f.Right = true;
                    break;
                case Target.LSTICK: Dir(t.Value, ref lx, ref ly); break;
                case Target.RSTICK: Dir(t.Value, ref rx, ref ry); break;
                case Target.WALK: walk = true; break;
            }
        }
        double tilt = walk ? cfg.WalkPercent / 100.0 : 1.0;
        Stick(lx, ly, tilt, ref f.LX, ref f.LY);
        Stick(rx, ry, tilt, ref f.RX, ref f.RY);
        return any;
    }

    static void Dir(int d, ref int x, ref int y)
    {
        if (d == 0) y++;
        else if (d == 1) y--;
        else if (d == 2) x--;
        else x++;
    }

    // Keys override the controller's stick while they're held. Diagonals stay on the circle.
    static void Stick(int x, int y, double tilt, ref byte bx, ref byte by)
    {
        if (x == 0 && y == 0) return;
        double m = tilt * (x != 0 && y != 0 ? 0.7071 : 1.0);
        bx = Mapper.ToByte(x * m);
        by = Mapper.ToByte(-y * m);
    }
}

// ------------------------------------------------------------------ settings (settings.ini)

class Settings
{
    public string Pi = "switchpad.local";
    public int Pad = -1;                 // XInput slot, -1 = auto
    public bool Ps3 = false;             // console: false = Nintendo Switch, true = PS3 (the Pi is told to switch)
    public bool PiLowPower = false;      // low-power mode on the Pi (Pi 4 on a weak USB port); the Pi is told
    public bool Positional = true;       // layout: true = position, false = label (Switch only)
    public int TriggerThreshold = 30;    // LT/RT value (0-255) above which ZL/ZR count as pressed
    public int Deadzone = 0;             // percent of stick travel ignored around the centre
    public int Interval = 1;             // minimum ms between packets
    public bool Keyboard = true;
    public bool BlockKeys = false;       // mapped keys don't reach other programs
    public bool RemoteKeysOnly = false;  // only injected keys (e.g. from Parsec) count
    public int WalkPercent = 50;
    public bool StartMinimized = false;
    public string PiUser = "";           // SSH user on the Pi, for Collect logs / Update Pi
    public Dictionary<string, int[]> KeyMap = Targets.DefaultMap();

    public static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); } }

    public Settings Clone()
    {
        var s = (Settings)MemberwiseClone();
        s.KeyMap = new Dictionary<string, int[]>();
        foreach (var kv in KeyMap) s.KeyMap[kv.Key] = (int[])kv.Value.Clone();
        return s;
    }

    public int[] Keys(string id)
    {
        int[] k;
        return KeyMap.TryGetValue(id, out k) ? k : new int[2];
    }

    public string Summary()
    {
        return string.Format("{6}, Pi {0}, pad {1}, layout {2}, triggers above {3}, deadzone {4}%, keyboard {5}",
            Pi, Pad < 0 ? "auto" : Pad.ToString(), Positional || Ps3 ? "position" : "label", TriggerThreshold, Deadzone,
            Keyboard ? string.Format("on (hide mapped keys {0}, remote keys only {1}, walk {2}%)",
                OnOff(BlockKeys), OnOff(RemoteKeysOnly), WalkPercent) : "off",
            ConsoleName) + (PiLowPower ? ", Pi low-power mode" : "");
    }

    public string ConsoleName { get { return Ps3 ? "PS3" : "Switch"; } }
    public string ConsoleId { get { return Ps3 ? "ps3" : "switch"; } }

    static string OnOff(bool b) { return b ? "on" : "off"; }

    static bool Bool(string v)
    {
        v = v.ToLowerInvariant();
        if (v == "on" || v == "true" || v == "yes" || v == "1") return true;
        if (v == "off" || v == "false" || v == "no" || v == "0") return false;
        throw new FormatException();
    }

    static int Int(string v, int min, int max) { return Math.Max(min, Math.Min(max, int.Parse(v, CultureInfo.InvariantCulture))); }

    public static Settings Load()
    {
        var s = new Settings();
        string path = FilePath;
        if (!File.Exists(path)) return s;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e) { Log.Write("Could not read " + path + ": " + e.Message + " (using defaults)"); return s; }
        string section = "";
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line[0] == '[')
            {
                section = line.Trim('[', ']').Trim().ToLowerInvariant();
                if (section == "keys") s.KeyMap = Targets.EmptyMap();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
            try
            {
                if (section == "keys") s.SetKeys(k, v);
                else s.Set(k.ToLowerInvariant(), v);
            }
            catch (Exception) { Log.Write("settings.ini: ignoring the line '" + line + "'"); }
        }
        return s;
    }

    void Set(string k, string v)
    {
        switch (k)
        {
            case "pi": if (v.Length > 0) Pi = v; break;
            case "pad": Pad = v.ToLowerInvariant() == "auto" ? -1 : Int(v, 0, 3); break;
            case "console":
                v = v.ToLowerInvariant();
                if (v != "switch" && v != "ps3") throw new FormatException();
                Ps3 = v == "ps3";
                break;
            case "layout":
                v = v.ToLowerInvariant();
                if (v != "position" && v != "label") throw new FormatException();
                Positional = v == "position";
                break;
            case "trigger_threshold": TriggerThreshold = Int(v, 1, 254); break;
            case "deadzone": Deadzone = Int(v, 0, 50); break;
            case "interval": Interval = Int(v, 0, 50); break;
            case "keyboard": Keyboard = Bool(v); break;
            case "block_keys": BlockKeys = Bool(v); break;
            case "remote_keys_only": RemoteKeysOnly = Bool(v); break;
            case "walk_percent": WalkPercent = Int(v, 10, 100); break;
            case "start_minimized": StartMinimized = Bool(v); break;
            case "pi_user": PiUser = v; break;
            case "pi_low_power": PiLowPower = Bool(v); break;
            default: throw new FormatException();
        }
    }

    void SetKeys(string id, string v)
    {
        var t = Targets.Find(id);
        if (t == null) throw new FormatException();
        var keys = new int[2];
        int n = 0;
        foreach (string part in v.Split(','))
        {
            if (part.Trim().Length == 0) continue;
            if (n < 2) keys[n++] = KeyNames.Parse(part);
        }
        KeyMap[t.Id] = keys;
    }

    public bool Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SwitchPad settings. Change them in SwitchPad.exe, or edit this file while SwitchPad is closed.");
        sb.AppendLine("pi=" + Pi);
        sb.AppendLine("pad=" + (Pad < 0 ? "auto" : Pad.ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine("console=" + ConsoleId);
        sb.AppendLine("layout=" + (Positional ? "position" : "label"));
        sb.AppendLine("trigger_threshold=" + TriggerThreshold);
        sb.AppendLine("deadzone=" + Deadzone);
        sb.AppendLine("interval=" + Interval);
        sb.AppendLine("keyboard=" + OnOff(Keyboard));
        sb.AppendLine("block_keys=" + OnOff(BlockKeys));
        sb.AppendLine("remote_keys_only=" + OnOff(RemoteKeysOnly));
        sb.AppendLine("walk_percent=" + WalkPercent);
        sb.AppendLine("start_minimized=" + OnOff(StartMinimized));
        sb.AppendLine("pi_user=" + PiUser);
        sb.AppendLine("pi_low_power=" + OnOff(PiLowPower));
        sb.AppendLine();
        sb.AppendLine("[keys]");
        sb.AppendLine("# Switch input = up to 2 Windows key names, e.g. W, Space, LShiftKey, D1 (the 1 key), NumPad8, Up");
        sb.AppendLine("# (PS3: A = Circle, B = Cross, X = Triangle, Y = Square, L/R = L1/R1, ZL/ZR = L2/R2, Plus = Start, Minus = Select, Home = PS)");
        foreach (var t in Targets.All) sb.AppendLine(t.Id + "=" + KeyNames.ToText(Keys(t.Id)));
        try
        {
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception e)
        {
            Log.Write("Could not save " + FilePath + ": " + e.Message);
            return false;
        }
    }
}

// ------------------------------------------------------------------ network (UDP) link to the Pi

// Pi -> PC health, every 2 s: "SWH1" + UTF-8 "key=value" lines (health_msg in switchpad.py).
// Older Pi software doesn't send it.
class PiHealth
{
    public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
    public double ReceivedMs;

    public static PiHealth Parse(byte[] d, int offset)
    {
        var h = new PiHealth();
        h.ReceivedMs = Clock.Ms;
        string text = Encoding.UTF8.GetString(d, offset, d.Length - offset);
        foreach (string line in text.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) h.Values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return h;
    }

    public string this[string key] { get { string v; return Values.TryGetValue(key, out v) ? v : ""; } }

    public double Num(string key)
    {
        double v;
        return double.TryParse(this[key], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
    }

    // get_throttled bits, or -1 if unknown.
    public long PowerFlags
    {
        get
        {
            string s = this["power"];
            if (s.StartsWith("0x")) s = s.Substring(2);
            long v;
            return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : -1;
        }
    }

    public string LinkName
    {
        get
        {
            switch (this["link"])
            {
                case "ethernet": return "Ethernet";
                case "wifi": return "Wi-Fi";
                case "none": return "no network";
                default: return this["link"];
            }
        }
    }

    public string Summary()
    {
        string console = this["console"] == "ps3"
            ? ", PS3 mode (PS button: " + (this["ps_button"].Length > 0 ? this["ps_button"] : "?") + ")"
            : this["console"] == "switch" ? ", Switch mode" : "";
        string low = this["lowpower"] == "on" ? ", low-power mode (" + this["lowpower_text"] + ")" : "";
        return string.Format("{0}, SwitchPad {1}, {2}, CPU {3} C, power {4}, USB {5}{6}{7}",
            this["model"], this["sw"], this["link_text"], this["temp"].Length > 0 ? this["temp"] : "?",
            this["power_text"], this["usb"], console, low);
    }
}

// The Pi's answer to a command from the PC ("SWR1" + nonce + text). A command can get more than
// one answer: the self-test answers "ok" at once and "done" when it has finished.
class CommandReply
{
    public string Command, Text;
    public bool IsError { get { return Text.StartsWith("error"); } }
}

class UdpLink
{
    readonly UdpClient udp;
    readonly int port;
    readonly object gate = new object();
    readonly AutoResetEvent lookupNow = new AutoResetEvent(false);
    volatile string host;
    volatile IPEndPoint target;
    volatile byte[] lastReport = Report.Neutral();
    volatile bool attached, discoverHit;
    int seq;
    long lastStatusMs = -100000; // Clock.Ms when the Pi last answered
    long discoverUntilMs = -1;
    string found;
    volatile PiHealth health;
    readonly LinkStats stats = new LinkStats();
    public LinkStats Stats { get { return stats; } }
    public PiHealth Health { get { return health; } }

    // Commands to the Pi: resent until the Pi answers (UDP can drop a packet), then kept a
    // while so later answers (the self-test's "done") can be matched to them.
    class PendingCommand
    {
        public uint Nonce;
        public string Command;
        public double FirstMs, SentMs;
        public int Tries;
        public bool Answered;
        public readonly List<string> Seen = new List<string>(); // answers already passed on
    }
    readonly object cmdGate = new object();
    readonly List<PendingCommand> pending = new List<PendingCommand>();
    readonly List<CommandReply> replies = new List<CommandReply>();
    uint nextNonce = (uint)Environment.TickCount;

    public UdpLink(string host, int port)
    {
        this.port = port;
        udp = new UdpClient(0, AddressFamily.InterNetwork);
        udp.EnableBroadcast = true; // for "Find Pi"
        // Ignore ICMP "port unreachable" (otherwise Receive throws WSAECONNRESET while the Pi boots).
        const int SIO_UDP_CONNRESET = -1744830452;
        udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        SetHost(host);
        // Looking up a name like switchpad.local can take 10+ s on Windows. Never do it in the
        // sending loop: look it up in the background and keep using the last address meanwhile.
        var t = new Thread(ResolveLoop);
        t.IsBackground = true;
        t.Start();
    }

    public string Host { get { return host; } }
    public string Address { get { var t = target; return t == null ? "" : t.Address.ToString(); } }

    public void SetHost(string h)
    {
        lock (gate)
        {
            host = h;
            IPAddress ip;
            target = IPAddress.TryParse(h, out ip) ? new IPEndPoint(ip, port) : null;
            Interlocked.Exchange(ref lastStatusMs, -100000);
            stats.Rtt.Reset();
            health = null;
        }
        lookupNow.Set();
    }

    double SinceStatusMs { get { return Clock.Ms - Interlocked.Read(ref lastStatusMs); } }
    public bool Online { get { return SinceStatusMs < 2000; } }
    public bool SwitchAttached { get { return attached; } }
    public string Status
    {
        get
        {
            var t = target;
            return "Pi " + (Online ? "online (" + t.Address + ")" : t == null ? "looking up " + host + "..." : "no reply from " + t.Address);
        }
    }

    void ResolveLoop()
    {
        bool first = true;
        while (true)
        {
            string h = host;
            IPAddress literal;
            // Look up again only if the Pi has been silent for a while (its address may have changed).
            if (!IPAddress.TryParse(h, out literal) && (target == null || SinceStatusMs > 5000))
            {
                var t0 = Stopwatch.StartNew();
                try
                {
                    IPAddress ip = null;
                    foreach (var a in Dns.GetHostAddresses(h))
                        if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                    if (ip == null) Log.Write("Network: " + h + " has no IPv4 address");
                    else
                    {
                        lock (gate)
                        {
                            if (host == h && (target == null || !target.Address.Equals(ip)))
                            {
                                Log.Write(string.Format(CultureInfo.InvariantCulture,
                                    "Network: {0} is {1} (lookup took {2:0.0} s{3})", h, ip,
                                    t0.ElapsedMilliseconds / 1000.0,
                                    first ? "; use the IP address as the Pi address to skip the lookup" : ""));
                                target = new IPEndPoint(ip, port);
                                first = false;
                            }
                        }
                    }
                }
                catch (SocketException e)
                {
                    Log.Write("Network: can't look up " + h + " (" + e.SocketErrorCode + ", after " +
                              t0.ElapsedMilliseconds + " ms), retrying");
                }
                lookupNow.WaitOne(5000);
            }
            else lookupNow.WaitOne(250);
        }
    }

    public void Send(byte[] report)
    {
        lastReport = report;
        var t = target;
        if (t == null) return;
        SendTo(report, t);
    }

    void SendTo(byte[] report, IPEndPoint to)
    {
        uint s = (uint)(Interlocked.Increment(ref seq) - 1);
        var pkt = Report.Packet(s, report);
        try { stats.Rtt.Sent(s); udp.Send(pkt, pkt.Length, to); stats.Sent++; stats.TotalSent++; }
        catch (SocketException e)
        {
            if (stats.SendErrors++ == 0) Log.Write("Network: send failed to " + to.Address + " (" + e.SocketErrorCode + ")");
        }
    }

    // "Find Pi": for 3 s, send the current input to every local network's broadcast address.
    // The Pi answers whoever sends it input, so its answer tells us its address.
    public void Discover()
    {
        Interlocked.Exchange(ref discoverUntilMs, (long)Clock.Ms + 3000);
        discoverHit = false;
        Log.Write("Find Pi: asking every network this PC is on...");
        var th = new Thread(() =>
        {
            var addrs = BroadcastAddresses();
            while (Clock.Ms < Interlocked.Read(ref discoverUntilMs))
            {
                foreach (var a in addrs) SendTo(lastReport, new IPEndPoint(a, port));
                Thread.Sleep(250);
            }
            if (!discoverHit) Log.Write("Find Pi: no answer (asked " + addrs.Count + " broadcast addresses)");
        });
        th.IsBackground = true;
        th.Start();
    }

    // Address found by the last "Find Pi", once (null if none).
    public string TakeFound() { return Interlocked.Exchange(ref found, null); }

    static List<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    byte[] ip = ua.Address.GetAddressBytes(), mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask[0] == 0) continue;
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    var addr = new IPAddress(b);
                    if (!list.Contains(addr)) list.Add(addr);
                }
            }
        }
        catch (Exception e) { Log.Write("Find Pi: can't list network adapters: " + e.Message); }
        return list;
    }

    static bool Magic(byte[] d, string m)
    {
        return d.Length >= 4 && d[0] == m[0] && d[1] == m[1] && d[2] == m[2] && d[3] == m[3];
    }

    public void Poll()
    {
        while (udp.Available > 0)
        {
            IPEndPoint from = null;
            byte[] d;
            try { d = udp.Receive(ref from); } catch (SocketException) { break; }
            if (d.Length < 8) continue;
            bool isStatus = StatusMsg.IsMagic(d, 0), isHealth = Magic(d, "SWH1"), isReply = Magic(d, "SWR1");
            if (!isStatus && !isHealth && !isReply) continue;
            if (isStatus && d.Length < StatusMsg.Length(d, 0)) continue;
            var t = target;
            if (t == null || !from.Address.Equals(t.Address))
            {
                // not from our Pi (unless "Find Pi" is asking around)
                if (!isStatus || Clock.Ms >= Interlocked.Read(ref discoverUntilMs)) continue;
                string addr = from.Address.ToString();
                lock (gate)
                {
                    host = addr;
                    target = new IPEndPoint(from.Address, port);
                }
                discoverHit = true;
                Interlocked.Exchange(ref discoverUntilMs, -1);
                Interlocked.Exchange(ref found, addr);
                Log.Write("Find Pi: the Pi answered from " + addr);
            }
            if (isHealth) { health = PiHealth.Parse(d, 4); continue; }
            if (isReply) { GotReply(BitConverter.ToUInt32(d, 4), Encoding.UTF8.GetString(d, 8, d.Length - 8)); continue; }
            Interlocked.Exchange(ref lastStatusMs, (long)Clock.Ms);
            attached = StatusMsg.Read(d, 0, stats);
        }
        ServiceCommands();
    }

    // Sends a command ("selftest", "restart", "reboot", "ping"); answers come from TakeReplies().
    public void SendCommand(string command)
    {
        var p = new PendingCommand { Command = command, FirstMs = Clock.Ms, SentMs = Clock.Ms };
        lock (cmdGate)
        {
            p.Nonce = nextNonce++;
            pending.Add(p);
        }
        SendCommandPacket(p);
    }

    void SendCommandPacket(PendingCommand p)
    {
        var t = target;
        p.SentMs = Clock.Ms;
        p.Tries++;
        if (t == null) return;
        byte[] text = Encoding.UTF8.GetBytes(p.Command);
        var pkt = new byte[8 + text.Length];
        pkt[0] = (byte)'S'; pkt[1] = (byte)'W'; pkt[2] = (byte)'C'; pkt[3] = (byte)'1';
        BitConverter.GetBytes(p.Nonce).CopyTo(pkt, 4);
        text.CopyTo(pkt, 8);
        try { udp.Send(pkt, pkt.Length, t); }
        catch (SocketException e) { Log.Write("Network: sending the command failed (" + e.SocketErrorCode + ")"); }
    }

    void GotReply(uint nonce, string text)
    {
        lock (cmdGate)
        {
            string command = "?";
            foreach (var p in pending)
            {
                if (p.Nonce != nonce) continue;
                command = p.Command;
                // the same answer again (a resend crossed the first answer): pass it on once
                if (p.Seen.Contains(text)) return;
                p.Seen.Add(text);
                p.Answered = true;
            }
            replies.Add(new CommandReply { Command = command, Text = text });
        }
    }

    void ServiceCommands()
    {
        var resend = new List<PendingCommand>();
        lock (cmdGate)
        {
            double now = Clock.Ms;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (p.Answered)
                {
                    if (now - p.FirstMs > 60000) pending.RemoveAt(i);
                }
                else if (now - p.SentMs < 300) { }
                else if (p.Tries < 4) resend.Add(p);
                else
                {
                    pending.RemoveAt(i);
                    replies.Add(new CommandReply
                    {
                        Command = p.Command,
                        Text = "error: no answer from the Pi. Is it on? If it is, its SwitchPad is older " +
                               "than this PC's and doesn't know commands yet: use Update Pi.",
                    });
                }
            }
        }
        foreach (var p in resend) SendCommandPacket(p);
    }

    public List<CommandReply> TakeReplies()
    {
        lock (cmdGate)
        {
            var r = new List<CommandReply>(replies);
            replies.Clear();
            return r;
        }
    }
}

// ------------------------------------------------------------------ engine (shared by both programs)

// What the window shows. A new one is made ~30 times a second; never changed after that.
class Snapshot
{
    public bool Online, Attached, Testing, Keyboard, KeyboardHeld;
    public string LinkText = "", Host = "", Address = "", Slots = "none";
    public double Ping = -1;
    public byte[] Current = Report.Neutral();
    public int PadSlot = -1, SentPerSec, RepliesPerSec;
    public double[] PingHistory = new double[0];
    public PiHealth Health;              // null until the Pi sends one (older Pi software never does)
    public double OnlineSinceMs = -1;    // Clock.Ms when the Pi started answering, -1 while it doesn't
    public string[] CommandLines = new string[0]; // recent commands and the Pi's answers
}

class Engine
{
    public volatile Settings Cfg;
    public volatile Snapshot Snap = new Snapshot();
    public volatile bool StopRequested;
    volatile bool testRequested;
    public readonly UdpLink Link;
#pragma warning disable 649 // set by the console program only
    public bool ConsoleStatus, TestForever;
    public int Seconds;
#pragma warning restore 649

    public Engine(Settings s)
    {
        Cfg = s;
        KeyboardHook.SetConfig(s);
        Link = new UdpLink(s.Pi, 5005);
    }

    // New settings from the window (it never changes an object it handed over).
    public void Apply(Settings s)
    {
        Settings old = Cfg;
        Cfg = s;
        KeyboardHook.SetConfig(s);
        if (s.Pi != Link.Host) Link.SetHost(s.Pi);
        string summary = s.Summary();
        if (summary != old.Summary()) Log.Write("Settings: " + summary);
    }

    // 12 s of the scripted test pattern.
    public void StartTest() { testRequested = true; }

    // A command for the Pi: "selftest", "restart", "reboot" or "ping".
    public void Command(string command)
    {
        Log.Write("Command to the Pi: " + command);
        AddCommandLine("→ " + command);
        Link.SendCommand(command);
    }

    readonly List<string> commandLines = new List<string>();

    void AddCommandLine(string text)
    {
        lock (commandLines)
        {
            commandLines.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text);
            if (commandLines.Count > 8) commandLines.RemoveAt(0);
        }
    }

    string[] CommandLinesCopy() { lock (commandLines) return commandLines.ToArray(); }

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        byte[] last = null;
        long lastSend = -1000, lastPrint = -1000, lastScan = -1000, lastSlotScan = -1000, lastStats = 0, prevIter = 0;
        long lastSecond = 0, lastSnap = -1000, testStart = -1, keyHeldAt = -100000;
        int pad = -1, padSetting = -2;
        string slots = "none", lastButtons = null;
        bool wasOnline = false, wasAttached = false;
        // stats, reset every 10 s
        int changes = 0, slowSends = 0;
        long maxSendMs = 0, maxLoopMs = 0;
        var sendTimer = new Stopwatch();
        var frame = new Frame();
        var ping = new double[60];
        for (int i = 0; i < ping.Length; i++) ping[i] = -1;
        long sent0 = 0, rx0 = 0;
        int sentPerSec = 0, rxPerSec = 0;
        double onlineSince = -1;
        PiHealth lastHealth = null;
        string healthKey = null;
        double lastConsoleCmdMs = -100000, lastLowPowerCmdMs = -100000;
        bool warnedOldPi = false, warnedOldPiLowPower = false;

        Settings c0 = Cfg;
        Log.Write(string.Format("Sending to the Pi at {0} ({1}, {2}, keyboard {3}, min interval {4} ms).{5}", c0.Pi,
            c0.ConsoleName + ", " + (TestForever ? "TEST PATTERN" : "layout " + (c0.Positional || c0.Ps3 ? "position" : "label")),
            c0.Pad >= 0 ? "pad " + c0.Pad : "pad auto", c0.Keyboard ? "on" : "off", c0.Interval,
            ConsoleStatus ? " Ctrl+C to quit." : ""));

        while (!StopRequested && (Seconds <= 0 || clock.ElapsedMilliseconds < Seconds * 1000L))
        {
            long now = clock.ElapsedMilliseconds;
            maxLoopMs = Math.Max(maxLoopMs, now - prevIter);
            prevIter = now;
            Settings cfg = Cfg;

            // Which XInput slots exist (Parsec adds/removes its virtual pads as guests come and go).
            if (now - lastSlotScan >= 1000)
            {
                lastSlotScan = now;
                string sl = Bridge.SlotList();
                if (sl != slots) { Log.Write("XInput pads connected in slots: " + sl); slots = sl; }
            }
            if (cfg.Pad != padSetting) { padSetting = cfg.Pad; pad = -1; lastScan = -1000; }

            if (testRequested)
            {
                testRequested = false;
                testStart = now;
                Log.Write("Test pattern for 12 s: L+R three times, D-pad right and left, left stick circle");
            }
            if (testStart >= 0 && now - testStart >= 12000) { testStart = -1; Log.Write("Test pattern finished"); }
            bool testing = TestForever || testStart >= 0;

            byte[] report;
            if (testing)
            {
                report = TestScript.At(TestForever ? now : now - testStart);
            }
            else
            {
                frame.Clear();
                XInputState st = new XInputState();
                bool have = pad >= 0 && XInput.TryGet(pad, out st);
                if (!have && now - lastScan >= 1000)
                {
                    lastScan = now;
                    int p = Bridge.PickPad(cfg.Pad);
                    if (p != pad)
                    {
                        Log.Write(p >= 0 ? "Using XInput pad in slot " + p
                                         : (pad >= 0 ? "Pad in slot " + pad + " disappeared. " : "") + "Waiting for a controller...");
                        pad = p;
                    }
                    if (pad >= 0) have = XInput.TryGet(pad, out st);
                }
                if (have) Mapper.FromXInput(st, cfg, frame);
                if (cfg.Keyboard && KeyMapper.Apply(frame, cfg)) keyHeldAt = now;
                report = Report.FromFrame(frame);
            }

            // Every button / D-pad change goes into the log file (sticks are summarized in stats).
            string buttons = ButtonsOnly(report, cfg.Ps3);
            if (buttons != lastButtons) { Log.Write("input: " + buttons, false); lastButtons = buttons; }

            // Send on change (at most every `interval` ms, always the newest state),
            // plus a 20 Hz heartbeat so the Pi's failsafe stays happy.
            long since = now - lastSend;
            if ((!Report.Same(report, last) && since >= cfg.Interval) || since >= 50)
            {
                if (!Report.Same(report, last)) changes++;
                sendTimer.Restart();
                Link.Send(report);
                long ms = sendTimer.ElapsedMilliseconds;
                maxSendMs = Math.Max(maxSendMs, ms);
                if (ms >= 20) slowSends++;
                last = report;
                lastSend = now;
            }
            Link.Poll();

            if (Link.Online != wasOnline)
            {
                wasOnline = Link.Online;
                onlineSince = wasOnline ? Clock.Ms : -1;
                Log.Write(wasOnline ? "Pi is answering (" + Link.Status + ")" : "Pi is NOT answering any more (" + Link.Status + ")");
            }
            var health = Link.Health;
            if (health != null && health != lastHealth)
            {
                lastHealth = health;
                // logged when something that matters changes (not every temperature wiggle)
                string key = health["sw"] + "|" + health["link"] + "|" + health["dev"] + "|" + health["power"] + "|" +
                             health["usb"] + "|" + health["console"] + "|" + health["ps_button"] + "|" +
                             health["lowpower_text"];
                if (key != healthKey) { healthKey = key; Log.Write("Pi health: " + health.Summary()); }
            }
            // The console is a setting on the PC; the Pi keeps its own copy on the SD card. Tell it
            // whenever the two differ (it answers in its health, every 2 s).
            if (health != null && Link.Online && Clock.Ms - health.ReceivedMs < 5000)
            {
                string piConsole = health["console"];
                if (piConsole.Length == 0)
                {
                    if (cfg.Ps3 && !warnedOldPi)
                    {
                        warnedOldPi = true;
                        Log.Write("The Pi's SwitchPad is older than this PC's and has no PS3 mode: use Update Pi.");
                        AddCommandLine("error: the Pi's SwitchPad has no PS3 mode yet. Use Update Pi.");
                    }
                }
                else if (piConsole != cfg.ConsoleId && Clock.Ms - lastConsoleCmdMs >= 6000)
                {
                    lastConsoleCmdMs = Clock.Ms;
                    Command("console " + cfg.ConsoleId);
                }

                // Low-power mode works the same way.
                string piLow = health["lowpower"], wantLow = cfg.PiLowPower ? "on" : "off";
                if (piLow.Length == 0)
                {
                    if (cfg.PiLowPower && !warnedOldPiLowPower)
                    {
                        warnedOldPiLowPower = true;
                        Log.Write("The Pi's SwitchPad is older than this PC's and has no low-power mode: use Update Pi.");
                        AddCommandLine("error: the Pi's SwitchPad has no low-power mode yet. Use Update Pi.");
                    }
                }
                else if (piLow != wantLow && Clock.Ms - lastLowPowerCmdMs >= 8000)
                {
                    lastLowPowerCmdMs = Clock.Ms;
                    Command("lowpower " + wantLow);
                }
            }
            foreach (var r in Link.TakeReplies())
            {
                Log.Write("Pi answered '" + r.Command + "': " + r.Text);
                AddCommandLine(r.Text);
            }
            if (wasOnline && Link.SwitchAttached != wasAttached)
            {
                wasAttached = Link.SwitchAttached;
                Log.Write(wasAttached ? "Pi reports: the console/PC is using the controller (USB configured)"
                                      : "Pi reports: console NOT connected on USB");
            }

            if (now - lastStats >= 10000)
            {
                var s = Link.Stats;
                Log.Write(string.Format(
                    "stats {0:0}s: {1} | " + cfg.ConsoleName + ": {2} | pad slot {3} (slots {4}) | keyboard {5} | sent {6}, send errors {7}, " +
                    "changes {8} | status msgs from Pi {9} | round trip PC-Pi-PC {10} | max send {11} ms, slow sends {12} | " +
                    "max loop {13} ms | holding: {14} | Pi: {15}",
                    (now - lastStats) / 1000.0, Link.Status,
                    Link.Online ? (Link.SwitchAttached ? "connected" : "not connected") : "?",
                    pad, slots, cfg.Keyboard ? "on" : "off", s.Sent, s.SendErrors, changes, s.StatusRx,
                    s.Rtt.TakeSummary(), maxSendMs, slowSends, maxLoopMs, Report.Describe(report, cfg.Ps3),
                    health != null && Clock.Ms - health.ReceivedMs < 10000
                        ? "cpu " + health["temp"] + " C, " + health["link_text"] + ", power " + health["power"]
                        : "no health info"), false);
                s.Sent = s.SendErrors = s.StatusRx = 0;
                changes = slowSends = 0;
                maxSendMs = maxLoopMs = 0;
                lastStats = now;
            }

            if (now - lastSecond >= 1000)
            {
                lastSecond = now;
                Array.Copy(ping, 1, ping, 0, ping.Length - 1);
                ping[ping.Length - 1] = Link.Online ? Link.Stats.Rtt.TakeSecond() : -1;
                var ls = Link.Stats;
                sentPerSec = (int)(ls.TotalSent - sent0); sent0 = ls.TotalSent;
                rxPerSec = (int)(ls.TotalStatusRx - rx0); rx0 = ls.TotalStatusRx;
            }

            if (now - lastSnap >= 30)
            {
                lastSnap = now;
                double rtt = Link.Stats.Rtt.Smooth;
                Snap = new Snapshot
                {
                    Online = Link.Online,
                    Attached = Link.SwitchAttached,
                    Testing = testing,
                    Keyboard = cfg.Keyboard,
                    KeyboardHeld = now - keyHeldAt < 1500,
                    LinkText = Link.Status,
                    Host = Link.Host,
                    Address = Link.Address,
                    Slots = slots,
                    Ping = Link.Online ? rtt : -1,
                    Current = report,
                    PadSlot = pad,
                    SentPerSec = sentPerSec,
                    RepliesPerSec = rxPerSec,
                    PingHistory = (double[])ping.Clone(),
                    Health = health,
                    OnlineSinceMs = onlineSince,
                    CommandLines = CommandLinesCopy(),
                };
            }

            if (ConsoleStatus && now - lastPrint >= 200)
            {
                lastPrint = now;
                double rtt = Link.Stats.Rtt.Smooth;
                Con.Status(string.Format("{0} | {4}: {1} | Ping: {2} | Pressed: {3}",
                    Link.Status,
                    Link.Online ? (Link.SwitchAttached ? "connected" : "not connected") : "?",
                    Link.Online && rtt >= 0 ? string.Format("{0:0} ms", rtt) : "?",
                    Report.Describe(report, cfg.Ps3), cfg.ConsoleName));
            }
            Thread.Sleep(1);
        }

        for (int i = 0; i < 5; i++) { Link.Send(Report.Neutral()); Thread.Sleep(10); } // release everything
    }

    static string ButtonsOnly(byte[] r, bool ps3)
    {
        var copy = (byte[])r.Clone();
        copy[3] = copy[4] = copy[5] = copy[6] = 128; // ignore sticks
        return Report.Describe(copy, ps3);
    }
}

// Helpers shared by both programs.
static class Bridge
{
    public const string Version = "2026-11-27b (window, keyboard, Pi health + controls, PS3 mode, Pi low-power mode)";

    // Only one SwitchPad.exe / SwitchPadBridge.exe at a time: two would fight over the controller.
    public static Mutex SingleInstance()
    {
        bool created;
        var m = new Mutex(true, "Local\\SwitchPadBridge", out created);
        if (created) return m;
        m.Dispose();
        return null;
    }

    public static string WindowsBuild()
    {
        try
        {
            const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            return Microsoft.Win32.Registry.GetValue(key, "ProductName", "?") + " " +
                   Microsoft.Win32.Registry.GetValue(key, "CurrentBuild", "?") +
                   (Environment.Is64BitProcess ? " 64-bit" : " 32-bit");
        }
        catch (Exception) { return Environment.OSVersion.Version.ToString(); }
    }

    public static List<string> DeviceLines()
    {
        var lines = new List<string>();
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

    public static string SlotList()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 4; i++)
        {
            XInputState s;
            if (XInput.TryGet(i, out s)) sb.Append(sb.Length == 0 ? "" : ",").Append(i);
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    public static int PickPad(int fixedPad)
    {
        XInputState s;
        if (fixedPad >= 0) return XInput.TryGet(fixedPad, out s) ? fixedPad : -1;
        for (int i = 3; i >= 0; i--) if (XInput.TryGet(i, out s)) return i; // Parsec's pads usually take the later slots
        return -1;
    }
}

// ------------------------------------------------------------------ console program

#if !GUI
static class Program
{
    const string Usage =
@"SwitchPadBridge - console version of SwitchPad.exe. Sends an Xbox/XInput pad and the keyboard
to the SwitchPad Pi (Nintendo Switch or PS3). It uses settings.ini (made by SwitchPad.exe) if
there is one; the options below override it for this run.

Usage: SwitchPadBridge.exe [options]
  --pi HOST       the Pi's IP address or name (default: switchpad.local).
                  The IP address is faster, e.g. --pi 192.168.137.79
  --pad N         XInput slot 0-3 to read (default: auto = highest connected slot)
  --console C     switch (default) or ps3. The Pi switches its USB side to match (the
                  controller reconnects for a second) and keeps that until told otherwise.
                  PS3: Xbox A/B/X/Y -> Cross/Circle/Square/Triangle, LB/RB -> L1/R1,
                  LT/RT -> L2/R2, Back/Start -> Select/Start, Guide or Back+Start -> PS.
  --layout L      position (default): Xbox A/B/X/Y -> Switch B/A/Y/X (same physical spot)
                  label: Xbox A/B/X/Y -> Switch A/B/X/Y (same letter). Switch only.
  --low-power M   on or off (default: as in settings.ini, else off): low-power mode on the Pi, for a Pi 4 that
                  restarts by itself on a weak USB port. Slowest CPU speed, LEDs off,
                  Wi-Fi radio off while Ethernet is used, Ethernet at 100 Mbit/s.
  --no-keyboard   ignore the keyboard
  --interval MS   minimum time between packets (default: 1)
  --test          send a scripted test pattern instead of reading a pad
  --list          list XInput pads, then exit
  --seconds N     stop after N seconds (default: run until Ctrl+C)

Extra buttons: Guide or Back+Start = HOME, Back + right-stick click = CAPTURE.
Keyboard: WASD = left stick, arrows = right stick, J/K/U/I = B/A/Y/X, Q/E = L/R, 1/3 = ZL/ZR,
Enter = +, Backspace = -, H = HOME, F12 = CAPTURE. Change them in SwitchPad.exe.";

    static int Main(string[] args)
    {
        Settings cfg = Settings.Load();
        int seconds = 0;
        bool test = false, list = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--pi": case "--wifi": cfg.Pi = args[++i]; break; // --wifi: older name
                    case "--pad": cfg.Pad = int.Parse(args[++i]); if (cfg.Pad < 0 || cfg.Pad > 3) throw new FormatException(); break;
                    case "--console":
                        string c = args[++i].ToLowerInvariant();
                        if (c == "switch") cfg.Ps3 = false;
                        else if (c == "ps3") cfg.Ps3 = true;
                        else throw new FormatException();
                        break;
                    case "--low-power":
                        string lp = args[++i].ToLowerInvariant();
                        if (lp == "on") cfg.PiLowPower = true;
                        else if (lp == "off") cfg.PiLowPower = false;
                        else throw new FormatException();
                        break;
                    case "--layout":
                        string l = args[++i].ToLowerInvariant();
                        if (l == "position") cfg.Positional = true;
                        else if (l == "label") cfg.Positional = false;
                        else throw new FormatException();
                        break;
                    case "--interval": cfg.Interval = int.Parse(args[++i]); break;
                    case "--no-keyboard": cfg.Keyboard = false; break;
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

        if (list) { foreach (var l in Bridge.DeviceLines()) Console.WriteLine(l); return 0; }

        Mutex mutex = Bridge.SingleInstance();
        if (mutex == null)
        {
            Console.WriteLine("SwitchPad is already running (SwitchPad.exe in the tray, or another SwitchPadBridge.exe). Close it first.");
            return 2;
        }

        Native.DisableQuickEdit();
        Con.Start();
        Log.Open();
        Log.Write("=== SwitchPadBridge " + Bridge.Version + " started, log file: " + Log.FilePath);
        Log.Write("Arguments: " + (args.Length == 0 ? "(none)" : string.Join(" ", args)) +
                  " | Windows build " + Bridge.WindowsBuild(), false);
        Log.Write("Settings: " + cfg.Summary() + (File.Exists(Settings.FilePath) ? "" : " (no settings.ini, defaults)"), false);
        foreach (var l in Bridge.DeviceLines()) Log.Write(l, false);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("CRASH (unhandled): " + e.ExceptionObject);

        if (cfg.Keyboard && !test) KeyboardHook.Start();
        var engine = new Engine(cfg);
        engine.ConsoleStatus = true;
        engine.TestForever = test;
        engine.Seconds = seconds;

        Console.CancelKeyPress += (s, e) => { e.Cancel = true; engine.StopRequested = true; };
        Log.Write("Precise timer when in the background: " + Native.KeepTimerPrecise(), false);
        Native.timeBeginPeriod(1);
        try { engine.Run(); }
        catch (Exception e) { Log.Write("CRASH: " + e); throw; }
        finally
        {
            Native.timeEndPeriod(1);
            Log.Write("=== Stopped. Log file: " + Log.FilePath);
            Log.Close();
            Con.Flush();
            Console.WriteLine();
            GC.KeepAlive(mutex);
        }
        return 0;
    }
}
#endif
