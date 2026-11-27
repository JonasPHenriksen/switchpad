// SwitchPad.exe: the window + tray icon around the engine in SwitchPadBridge.cs.
// Built by build.cmd with /define:GUI; the console build skips this whole file.
#if GUI
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class GuiProgram
{
    public const string ShowEventName = "Local\\SwitchPadShow";
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args)
    {
        bool minimized = false;
        foreach (string a in args) if (a.ToLowerInvariant() == "--minimized") minimized = true;

        Mutex mutex = Bridge.SingleInstance();
        if (mutex == null)
        {
            // Already running: bring that window to the front instead.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); return 0; }
            catch (Exception) { }
            MessageBox.Show("SwitchPad is already running (maybe SwitchPadBridge.exe in a console window). Close it first.",
                "SwitchPad", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        try { SetProcessDPIAware(); } catch (Exception) { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Log.ToConsole = false;
        Log.Open();
        Log.Write("=== SwitchPad " + Bridge.Version + " (window) started, log file: " + Log.FilePath);
        Log.Write("Windows build " + Bridge.WindowsBuild(), false);
        foreach (var l in Bridge.DeviceLines()) Log.Write(l, false);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("CRASH (unhandled): " + e.ExceptionObject);
        Application.ThreadException += (s, e) =>
        {
            Log.Write("CRASH (window): " + e.Exception);
            MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nDetails are in the log file:\n" + Log.FilePath,
                "SwitchPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        Settings cfg = Settings.Load();
        Log.Write("Settings: " + cfg.Summary() + (File.Exists(Settings.FilePath) ? "" : " (no settings.ini yet, defaults)"));
        KeyboardHook.Start();
        var engine = new Engine(cfg);
        Log.Write("Precise timer when in the background: " + Native.KeepTimerPrecise(), false);
        Native.timeBeginPeriod(1);
        var thread = new Thread(() =>
        {
            try { engine.Run(); }
            catch (Exception e) { Log.Write("CRASH (engine): " + e); }
        });
        thread.IsBackground = true;
        thread.Name = "engine";
        thread.Priority = ThreadPriority.AboveNormal;
        thread.Start();
        try
        {
            Application.Run(new MainForm(engine, showEvent, minimized || cfg.StartMinimized));
        }
        finally
        {
            engine.StopRequested = true;
            thread.Join(2000);
            Native.timeEndPeriod(1);
            Log.Write("=== Stopped. Log file: " + Log.FilePath);
            Log.Close();
            GC.KeepAlive(mutex);
        }
        return 0;
    }
}

// The window is always dark.
static class Pal
{
    public static readonly Color Back = Color.FromArgb(0x0F, 0x12, 0x16);       // window and tab pages
    public static readonly Color Card = Color.FromArgb(0x18, 0x1C, 0x22);       // tiles, graph, pad picture
    public static readonly Color Raised = Color.FromArgb(0x22, 0x28, 0x30);     // buttons
    public static readonly Color Hover = Color.FromArgb(0x2C, 0x33, 0x3C);
    public static readonly Color Input = Color.FromArgb(0x12, 0x16, 0x1B);      // text boxes, lists
    public static readonly Color Border = Color.FromArgb(0x33, 0x3A, 0x44);
    public static readonly Color Grid = Color.FromArgb(0x26, 0x2C, 0x34);       // ping graph lines
    public static readonly Color Text = Color.FromArgb(0xE6, 0xED, 0xF3);
    public static readonly Color Muted = Color.FromArgb(0x9A, 0xA4, 0xAF);
    public static readonly Color Idle = Color.FromArgb(0x6E, 0x77, 0x81);
    public static readonly Color Good = Color.FromArgb(0x3F, 0xB9, 0x50);
    public static readonly Color Warn = Color.FromArgb(0xD2, 0x99, 0x22);
    public static readonly Color Bad = Color.FromArgb(0xF8, 0x51, 0x49);
    public static readonly Color Accent = Color.FromArgb(0x2F, 0x81, 0xF7);
    public static readonly Color Select = Color.FromArgb(0xF0, 0x88, 0x3E);
    public static readonly Color SelectBack = Color.FromArgb(0x1F, 0x3A, 0x5F); // selected list row
    public static readonly Color Body = Color.FromArgb(0x23, 0x29, 0x31);       // pad picture body
    public static readonly Color BodyEdge = Color.FromArgb(0x4A, 0x53, 0x5E);
    public static readonly Color Ring = Color.FromArgb(0x2B, 0x32, 0x3B);
    public static readonly Color Face = Color.FromArgb(0x33, 0x3B, 0x45);       // pad picture buttons
    public static readonly Color ButtonEdge = Color.FromArgb(0x66, 0x70, 0x7B);
}

// Dark versions of the standard Windows controls, the title bar and the tray menu.
static class Dark
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Dark title bar: attribute 20 on Windows 10 20H1+ / 11, 19 on older Windows 10 builds.
    public static void TitleBar(IntPtr hwnd)
    {
        int on = 1;
        try
        {
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
        }
        catch (Exception) { } // no DWM: keeps the normal title bar
    }

    // Dark scroll bars on text boxes and lists.
    static void Theme(Control c, string name)
    {
        Action set = () => { try { SetWindowTheme(c.Handle, name, null); } catch (Exception) { } };
        if (c.IsHandleCreated) set();
        else c.HandleCreated += (s, e) => set();
    }

    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls)
        {
            var b = c as Button;
            if (b != null)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = Pal.Raised;
                b.ForeColor = Pal.Text;
                b.FlatAppearance.BorderColor = Pal.Border;
                b.FlatAppearance.MouseOverBackColor = Pal.Hover;
                b.FlatAppearance.MouseDownBackColor = Pal.Border;
            }
            else if (c is TextBox)
            {
                var t = (TextBox)c;
                t.BackColor = Pal.Input;
                t.ForeColor = Pal.Text;
                t.BorderStyle = BorderStyle.FixedSingle;
                Theme(c, "DarkMode_Explorer");
            }
            else if (c is ListView)
            {
                var l = (ListView)c;
                l.BackColor = Pal.Input;
                l.ForeColor = Pal.Text;
                l.BorderStyle = BorderStyle.FixedSingle;
                Theme(c, "DarkMode_Explorer");
            }
            else if (c is ComboBox)
            {
                c.BackColor = Pal.Input;
                c.ForeColor = Pal.Text;
                continue; // DarkComboBox draws itself
            }
            else if (c is NumericUpDown)
            {
                var n = (NumericUpDown)c;
                n.BackColor = Pal.Input;
                n.ForeColor = Pal.Text;
                n.BorderStyle = BorderStyle.FixedSingle;
                // The arrows are drawn by WinForms in the light theme: paint dark ones over them.
                foreach (Control part in n.Controls)
                    if (!(part is TextBox)) part.Paint += PaintSpin;
                continue;
            }
            else if (c is CheckBox) continue; // DarkCheckBox draws itself
            else if (c is TabPage)
            {
                var p = (TabPage)c;
                p.UseVisualStyleBackColor = false;
                p.BackColor = Pal.Back;
                p.ForeColor = Pal.Text;
            }
            Apply(c);
        }
    }

    static void PaintSpin(object sender, PaintEventArgs e)
    {
        var c = (Control)sender;
        var g = e.Graphics;
        int w = c.Width, h = c.Height;
        using (var b = new SolidBrush(c.Enabled ? Pal.Raised : Pal.Input)) g.FillRectangle(b, 0, 0, w, h);
        using (var pen = new Pen(Pal.Border))
        {
            g.DrawLine(pen, 0, 0, 0, h);
            g.DrawLine(pen, 0, h / 2, w, h / 2);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = w / 2f, a = Math.Max(2.5f, Math.Min(w * 0.22f, h * 0.12f));
        float up = h / 4f, down = h * 3 / 4f;
        using (var b = new SolidBrush(c.Enabled ? Pal.Text : Pal.Idle))
        {
            g.FillPolygon(b, new[] { new PointF(cx - a, up + a / 2), new PointF(cx + a, up + a / 2), new PointF(cx, up - a / 2) });
            g.FillPolygon(b, new[] { new PointF(cx - a, down - a / 2), new PointF(cx + a, down - a / 2), new PointF(cx, down + a / 2) });
        }
    }

    public static void Menu(ContextMenuStrip m)
    {
        m.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
        m.BackColor = Pal.Card;
        m.ForeColor = Pal.Text;
        m.ShowImageMargin = false;
        m.ShowCheckMargin = true;
    }

    class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Pal.Card; } }
        public override Color MenuBorder { get { return Pal.Border; } }
        public override Color MenuItemBorder { get { return Pal.Hover; } }
        public override Color MenuItemSelected { get { return Pal.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Pal.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Pal.Hover; } }
        public override Color ImageMarginGradientBegin { get { return Pal.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Pal.Card; } }
        public override Color ImageMarginGradientEnd { get { return Pal.Card; } }
        public override Color SeparatorDark { get { return Pal.Border; } }
        public override Color SeparatorLight { get { return Pal.Card; } }
        public override Color CheckBackground { get { return Pal.SelectBack; } }
        public override Color CheckSelectedBackground { get { return Pal.SelectBack; } }
        public override Color CheckPressedBackground { get { return Pal.SelectBack; } }
        public override Color ButtonSelectedBorder { get { return Pal.Accent; } }
    }
}

// Tab strip drawn in the dark colours (the Windows one is always light).
class DarkTabControl : TabControl
{
    public DarkTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Pal.Back);
        float k = g.DpiX / 96f;
        if (TabCount == 0) return;
        Rectangle first = GetTabRect(0);
        int stripBottom = first.Bottom;
        using (var pen = new Pen(Pal.Border)) g.DrawLine(pen, 0, stripBottom, Width, stripBottom);
        for (int i = 0; i < TabCount; i++)
        {
            Rectangle r = GetTabRect(i);
            bool sel = i == SelectedIndex;
            if (sel)
            {
                using (var b = new SolidBrush(Pal.Card)) g.FillRectangle(b, r.X, r.Y, r.Width, r.Height + 1);
                using (var b = new SolidBrush(Pal.Accent)) g.FillRectangle(b, r.X, r.Bottom - 2 * k, r.Width, 2 * k);
            }
            TextRenderer.DrawText(g, TabPages[i].Text, Font, r, sel ? Pal.Text : Pal.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}

// A check box drawn in the dark colours (the Windows one stays light and hides its tick).
class DarkCheckBox : CheckBox
{
    bool hot;

    public DarkCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public override Size GetPreferredSize(Size proposed)
    {
        var s = base.GetPreferredSize(proposed);
        return new Size(s.Width + (int)(4 * ScreenDpi() / 96f), s.Height);
    }

    static float ScreenDpi() { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX; }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Pal.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = g.DpiX / 96f, s = 14 * k;
        var box = new RectangleF(1, (Height - s) / 2f, s, s);
        using (var path = Gfx.Rounded(box, 3 * k))
        {
            Color fill = Checked ? (Enabled ? Pal.Accent : Pal.Border) : Pal.Input;
            Color edge = Checked ? fill : !Enabled ? Pal.Border : hot ? Pal.Text : Pal.Muted;
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var pen = new Pen(edge, 1f)) g.DrawPath(pen, path);
        }
        if (Checked)
            using (var pen = new Pen(Enabled ? Color.White : Pal.Idle, 1.8f * k))
                g.DrawLines(pen, new[]
                {
                    new PointF(box.X + 3.2f * k, box.Y + s * 0.52f),
                    new PointF(box.X + s * 0.42f, box.Bottom - 3.6f * k),
                    new PointF(box.Right - 3.2f * k, box.Y + 3.6f * k),
                });
        int x = (int)(s + 7 * k);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), Enabled ? ForeColor : Pal.Idle,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
            using (var pen = new Pen(Pal.Accent) { DashStyle = DashStyle.Dot })
                g.DrawRectangle(pen, x - 2, 1, Width - x + 1, Height - 3);
    }
}

// A drop-down list drawn in the dark colours (the Windows one keeps a light frame).
class DarkComboBox : ComboBox
{
    const int WM_PAINT = 0x000F;

    public DarkComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
    }

    // The open list's rows.
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool box = (e.State & DrawItemState.ComboBoxEdit) != 0; // the closed box, not the open list
        bool sel = !box && (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? Pal.SelectBack : box && !Enabled ? Pal.Back : Pal.Input)) e.Graphics.FillRectangle(b, e.Bounds);
        TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, Rectangle.Inflate(e.Bounds, -3, 0), Enabled ? Pal.Text : Pal.Idle,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    // The closed box: painted over what Windows drew.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT) return;
        using (var g = Graphics.FromHwnd(Handle))
        {
            float k = g.DpiX / 96f;
            var r = new Rectangle(0, 0, Width, Height);
            bool hot = Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));
            using (var b = new SolidBrush(Enabled ? Pal.Input : Pal.Back)) g.FillRectangle(b, r);
            using (var pen = new Pen(Focused || hot ? Pal.Muted : Pal.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            int arrow = (int)(18 * k);
            string text = SelectedIndex >= 0 ? Items[SelectedIndex].ToString() : "";
            TextRenderer.DrawText(g, text, Font, new Rectangle((int)(4 * k), 0, Width - arrow - (int)(4 * k), Height),
                Enabled ? Pal.Text : Pal.Idle,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width - arrow / 2f - 1, cy = Height / 2f, a = 3.5f * k;
            using (var b = new SolidBrush(Enabled ? Pal.Muted : Pal.Idle))
                g.FillPolygon(b, new[] { new PointF(cx - a, cy - a / 2), new PointF(cx + a, cy - a / 2), new PointF(cx, cy + a / 2) });
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
}

// The key list, drawn dark (the Windows header and selection colours are light).
class DarkListView : ListView
{
    public DarkListView()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using (var b = new SolidBrush(Pal.Card)) e.Graphics.FillRectangle(b, e.Bounds);
        using (var pen = new Pen(Pal.Border))
        {
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
        }
        var r = Rectangle.Inflate(e.Bounds, -5, 0);
        TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, r, Pal.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e) { } // the sub-items draw the row

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        bool sel = e.Item.Selected;
        using (var b = new SolidBrush(sel ? Pal.SelectBack : BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
        var r = Rectangle.Inflate(e.Bounds, -5, 0);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, r, e.ColumnIndex == 0 || sel ? Pal.Text : Pal.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    // The last column fills the rest of the width, so no light header area is left over.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitLastColumn();
    }

    public void FitLastColumn()
    {
        if (Columns.Count == 0) return;
        int used = 0;
        for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
        int w = ClientSize.Width - used;
        if (w > 40 && Columns[Columns.Count - 1].Width != w) Columns[Columns.Count - 1].Width = w;
    }
}

static class Gfx
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static GraphicsPath Ellipse(RectangleF r)
    {
        var p = new GraphicsPath();
        p.AddEllipse(r);
        return p;
    }

    // Centred text that shrinks to fit the width.
    public static void Text(Graphics g, string s, RectangleF r, Color color, float size, bool bold)
    {
        if (string.IsNullOrEmpty(s)) return;
        var style = bold ? FontStyle.Bold : FontStyle.Regular;
        using (var sf = new StringFormat(StringFormatFlags.NoWrap))
        {
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            var f = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            try
            {
                float w = g.MeasureString(s, f, PointF.Empty, sf).Width;
                if (w > r.Width + 4 && w > 0)
                {
                    float fit = Math.Max(4.5f, size * (r.Width + 4) / w);
                    f.Dispose();
                    f = new Font("Segoe UI", fit, style, GraphicsUnit.Pixel);
                }
                var big = RectangleF.Inflate(r, 20, 0);
                using (var b = new SolidBrush(color)) g.DrawString(s, f, b, big, sf);
            }
            finally { f.Dispose(); }
        }
    }
}

// ------------------------------------------------------------------ controller picture

// Draws a Switch-style pad and lights up what the console is receiving (with PS3 symbols in PS3
// mode). With Mapping on, every button shows its key instead, and clicking a button picks it for
// a new key.
class PadView : Control
{
    public byte[] State = Report.Neutral();
    public bool Mapping;
    public Settings Cfg;
    public string Selected;
    public event Action<string> TargetClicked;
    readonly ToolTip tip = new ToolTip();
    string tipFor;
    float scale = 1, ox, oy;
    const float CW = 440, CH = 250;

    struct Part
    {
        public string Id, Text;
        public RectangleF R;
        public bool Circle;
        public Part(string id, string text, float x, float y, float w, float h, bool circle)
        {
            Id = id; Text = text; R = new RectangleF(x, y, w, h); Circle = circle;
        }
    }

    static readonly Part[] Parts =
    {
        new Part("ZL", "ZL", 85, 8, 95, 20, false),
        new Part("L", "L", 85, 32, 95, 18, false),
        new Part("ZR", "ZR", 260, 8, 95, 20, false),
        new Part("R", "R", 260, 32, 95, 18, false),
        new Part("Minus", "−", 176, 71, 18, 18, true),
        new Part("Plus", "+", 246, 71, 18, 18, true),
        new Part("Capture", "", 187, 102, 16, 16, false),
        new Part("Home", "⌂", 235, 100, 20, 20, true),
        new Part("X", "X", 294, 70, 22, 22, true),
        new Part("A", "A", 318, 94, 22, 22, true),
        new Part("B", "B", 294, 118, 22, 22, true),
        new Part("Y", "Y", 270, 94, 22, 22, true),
        new Part("DUp", "▲", 171, 123, 18, 18, false),
        new Part("DDown", "▼", 171, 159, 18, 18, false),
        new Part("DLeft", "◀", 153, 141, 18, 18, false),
        new Part("DRight", "▶", 189, 141, 18, 18, false),
    };
    static readonly PointF LStickC = new PointF(135, 105), RStickC = new PointF(260, 150);

    // PS3 mode: the same spots carry the PS3 buttons (the Pi sends Switch A as Circle etc.).
    static readonly Dictionary<string, string> Ps3Text = new Dictionary<string, string>
    {
        { "ZL", "L2" }, { "L", "L1" }, { "ZR", "R2" }, { "R", "R1" },
        { "Minus", "▪" }, { "Plus", "▸" }, { "Home", "PS" },
        { "X", "△" }, { "A", "○" }, { "B", "✕" }, { "Y", "□" },
    };

    bool Ps3 { get { return Cfg != null && Cfg.Ps3; } }

    string PartText(Part p)
    {
        string s;
        return Ps3 && Ps3Text.TryGetValue(p.Id, out s) ? s : p.Text;
    }

    public PadView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Pal.Card;
    }

    public void ResetTip() { tipFor = null; }

    void Fit()
    {
        scale = Math.Min(Width / CW, Height / CH);
        ox = (Width - CW * scale) / 2;
        oy = (Height - CH * scale) / 2;
    }

    bool Pressed(string id, int buttons, int hat)
    {
        switch (id)
        {
            case "DUp": return hat == 7 || hat == 0 || hat == 1;
            case "DRight": return hat == 1 || hat == 2 || hat == 3;
            case "DDown": return hat == 3 || hat == 4 || hat == 5;
            case "DLeft": return hat == 5 || hat == 6 || hat == 7;
        }
        var t = Targets.Find(id);
        return t != null && t.Kind == Target.BUTTON && (buttons & t.Value) != 0;
    }

    string KeyText(string id)
    {
        if (Cfg == null) return "";
        int[] k = Cfg.Keys(id);
        int a = k[0] != 0 ? k[0] : k[1], b = k[0] != 0 ? k[1] : 0;
        if (a == 0) return "–";
        string s = KeyNames.Short(a);
        if (b != 0)
        {
            string both = s + "/" + KeyNames.Short(b);
            s = both.Length <= 6 ? both : s + "…";
        }
        return s;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Width < 20 || Height < 20) return;
        Fit();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.TranslateTransform(ox, oy);
        g.ScaleTransform(scale, scale);

        byte[] r = State;
        int buttons = r[0] | (r[1] << 8), hat = r[2];

        // Body: outlines first, then the fills on top, so only the outer edge of the union shows.
        var body = new RectangleF(70, 55, 300, 130);
        var gripL = new RectangleF(75, 100, 115, 140);
        var gripR = new RectangleF(250, 100, 115, 140);
        using (var bodyPath = Gfx.Rounded(body, 48))
        {
            using (var pen = new Pen(Pal.BodyEdge, 3f))
            {
                g.DrawPath(pen, bodyPath);
                g.DrawEllipse(pen, gripL);
                g.DrawEllipse(pen, gripR);
            }
            using (var b = new SolidBrush(Pal.Body))
            {
                g.FillPath(b, bodyPath);
                g.FillEllipse(b, gripL);
                g.FillEllipse(b, gripR);
            }
        }

        // D-pad centre
        using (var b = new SolidBrush(Pal.Face)) g.FillRectangle(b, 171, 141, 18, 18);

        foreach (var p in Parts)
            if (!(Ps3 && p.Id == "Capture")) DrawPart(g, p, Pressed(p.Id, buttons, hat)); // no Capture on a PS3
        DrawStick(g, LStickC, r[3], r[4], "L", (buttons & Report.LCLICK) != 0);
        DrawStick(g, RStickC, r[5], r[6], "R", (buttons & Report.RCLICK) != 0);
    }

    void DrawPart(Graphics g, Part p, bool pressed)
    {
        bool sel = Mapping && Selected == p.Id;
        using (var path = p.Circle ? Gfx.Ellipse(p.R) : Gfx.Rounded(p.R, Math.Min(6, p.R.Height / 2)))
        {
            using (var b = new SolidBrush(pressed ? Pal.Accent : Pal.Face)) g.FillPath(b, path);
            using (var pen = new Pen(sel ? Pal.Select : Pal.ButtonEdge, sel ? 2.5f : 1.2f)) g.DrawPath(pen, path);
        }
        float size = Math.Min(10f, p.R.Height * 0.55f);
        if (Mapping)
        {
            string s = KeyText(p.Id);
            Gfx.Text(g, s, p.R, pressed ? Color.White : (s == "–" ? Pal.Idle : Pal.Text), Math.Max(6.5f, size), true);
        }
        else if (p.Id == "Capture")
        {
            using (var pen = new Pen(pressed ? Color.White : Pal.Muted, 1.2f))
                g.DrawEllipse(pen, p.R.X + 4.5f, p.R.Y + 4.5f, p.R.Width - 9, p.R.Height - 9);
        }
        else Gfx.Text(g, PartText(p), p.R, pressed ? Color.White : Pal.Text, size, true);
    }

    void DrawStick(Graphics g, PointF c, byte x, byte y, string side, bool clicked)
    {
        var ring = new RectangleF(c.X - 30, c.Y - 30, 60, 60);
        using (var b = new SolidBrush(Pal.Ring)) g.FillEllipse(b, ring);
        using (var pen = new Pen(Pal.ButtonEdge, 1.2f)) g.DrawEllipse(pen, ring);
        float dx = (x - 128) / 127.5f, dy = (y - 128) / 127.5f;

        if (!Mapping)
        {
            var cap = new RectangleF(c.X + dx * 14 - 18, c.Y + dy * 14 - 18, 36, 36);
            using (var b = new SolidBrush(clicked ? Pal.Accent : Pal.Face)) g.FillEllipse(b, cap);
            using (var pen = new Pen(Pal.ButtonEdge, 1.2f)) g.DrawEllipse(pen, cap);
            Gfx.Text(g, side + "S", cap, clicked ? Color.White : Pal.Idle, 8, false);
            return;
        }

        // Where the stick is now: a dot under the labels (small drift is ignored).
        if (Math.Abs(dx) > 0.15f || Math.Abs(dy) > 0.15f)
            using (var b = new SolidBrush(Color.FromArgb(150, Pal.Accent))) g.FillEllipse(b, c.X + dx * 24 - 4, c.Y + dy * 24 - 4, 8, 8);

        string[] dirs = { "Up", "Down", "Left", "Right" };
        float[] offX = { 0, 0, -19, 19 }, offY = { -19, 19, 0, 0 };
        bool[] active = { dy < -0.4f, dy > 0.4f, dx < -0.4f, dx > 0.4f };
        for (int i = 0; i < 4; i++)
        {
            string id = side + dirs[i];
            var rr = new RectangleF(c.X + offX[i] - 10, c.Y + offY[i] - 6, 20, 12);
            using (var path = Gfx.Rounded(rr, 4))
            {
                if (active[i]) using (var b = new SolidBrush(Pal.Accent)) g.FillPath(b, path);
                if (Selected == id) using (var pen = new Pen(Pal.Select, 2f)) g.DrawPath(pen, path);
            }
            string s = KeyText(id);
            Gfx.Text(g, s, rr, active[i] ? Color.White : (s == "–" ? Pal.Idle : Pal.Text), 9.5f, true);
        }
        string clickId = side + "S";
        var mid = new RectangleF(c.X - 9, c.Y - 9, 18, 18);
        using (var b = new SolidBrush(clicked ? Pal.Accent : Pal.Face)) g.FillEllipse(b, mid);
        using (var pen = new Pen(Selected == clickId ? Pal.Select : Pal.ButtonEdge, Selected == clickId ? 2f : 1f)) g.DrawEllipse(pen, mid);
        string ks = KeyText(clickId);
        Gfx.Text(g, ks, mid, clicked ? Color.White : (ks == "–" ? Pal.Idle : Pal.Text), 8f, true);
    }

    public string HitTest(Point pt)
    {
        if (scale <= 0) return null;
        var p = new PointF((pt.X - ox) / scale, (pt.Y - oy) / scale);
        foreach (var part in Parts)
        {
            if (Ps3 && part.Id == "Capture") continue;
            if (part.Circle)
            {
                float rx = part.R.Width / 2, ry = part.R.Height / 2;
                float ddx = (p.X - part.R.X - rx) / rx, ddy = (p.Y - part.R.Y - ry) / ry;
                if (ddx * ddx + ddy * ddy <= 1.2f) return part.Id;
            }
            else if (RectangleF.Inflate(part.R, 1, 1).Contains(p)) return part.Id;
        }
        return StickHit(LStickC, "L", p) ?? StickHit(RStickC, "R", p);
    }

    static string StickHit(PointF c, string side, PointF p)
    {
        float dx = p.X - c.X, dy = p.Y - c.Y;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d > 31) return null;
        if (d < 10) return side + "S";
        if (Math.Abs(dx) > Math.Abs(dy)) return side + (dx < 0 ? "Left" : "Right");
        return side + (dy < 0 ? "Up" : "Down");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string id = HitTest(e.Location);
        Cursor = Mapping && id != null ? Cursors.Hand : Cursors.Default;
        if (id == tipFor) return;
        tipFor = id;
        string text = "";
        if (id != null)
        {
            var t = Targets.Find(id);
            string keys = Cfg == null ? "" : KeyNames.ListPretty(Cfg.Keys(id));
            text = (t != null ? t.LabelFor(Ps3) : id) + (keys.Length == 0 ? ": no key" : ": " + keys) +
                   (Mapping ? "\nClick it, then press a key to change it." : "");
        }
        tip.SetToolTip(this, text);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!Mapping || e.Button != MouseButtons.Left) return;
        string id = HitTest(e.Location);
        if (id != null && TargetClicked != null) TargetClicked(id);
    }
}

// ------------------------------------------------------------------ small display controls

class StatusTile : Control
{
    public string Title = "", Value = "", Sub = "";
    public Color Dot = Pal.Idle;

    public StatusTile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void Set(string value, string sub, Color dot)
    {
        if (value == Value && sub == Sub && dot == Dot) return;
        Value = value; Sub = sub; Dot = dot;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : SystemColors.Control);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = g.DpiX / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Gfx.Rounded(r, 8 * k))
        {
            using (var b = new SolidBrush(Pal.Card)) g.FillPath(b, path);
            using (var pen = new Pen(Pal.Border)) g.DrawPath(pen, path);
        }
        int pad = (int)(12 * k);
        using (var b = new SolidBrush(Dot)) g.FillEllipse(b, pad, pad + 3 * k, 9 * k, 9 * k);
        var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        using (var f = new Font(Font.FontFamily, 8f, FontStyle.Bold))
            TextRenderer.DrawText(g, Title.ToUpperInvariant(), f,
                new Rectangle(pad + (int)(16 * k), pad, Width - 2 * pad, (int)(16 * k)), Pal.Muted, flags);
        using (var f = new Font(Font.FontFamily, 12.5f, FontStyle.Bold))
            TextRenderer.DrawText(g, Value, f, new Rectangle(pad, pad + (int)(19 * k), Width - 2 * pad, (int)(26 * k)), Pal.Text, flags);
        using (var f = new Font(Font.FontFamily, 8.5f))
            TextRenderer.DrawText(g, Sub, f, new Rectangle(pad, pad + (int)(46 * k), Width - 2 * pad, (int)(17 * k)), Pal.Muted, flags);
    }
}

class PingGraph : Control
{
    public double[] Data = new double[0];

    public PingGraph()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public static Color ColorFor(double ms) { return ms < 20 ? Pal.Good : ms < 50 ? Pal.Warn : Pal.Bad; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : SystemColors.Control);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = g.DpiX / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Gfx.Rounded(r, 8 * k))
        {
            using (var b = new SolidBrush(Pal.Card)) g.FillPath(b, path);
            using (var pen = new Pen(Pal.Border)) g.DrawPath(pen, path);
        }
        double[] d = Data;
        double max = 0, sum = 0, now = -1;
        int n = 0;
        foreach (double v in d) if (v >= 0) { max = Math.Max(max, v); sum += v; n++; now = v; }

        var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        int pad = (int)(10 * k);
        string head = n == 0 ? "No answers from the Pi yet" :
            string.Format(CultureInfo.InvariantCulture, "now {0:0} ms   avg {1:0} ms   max {2:0} ms", now, sum / n, max);
        TextRenderer.DrawText(g, head, Font, new Point(pad, pad), n == 0 ? Pal.Muted : Pal.Text, flags);

        var plot = new RectangleF(pad, pad + 24 * k, Width - 2 * pad - 34 * k, Height - 2 * pad - 24 * k);
        if (plot.Width < 10 || plot.Height < 10 || d.Length == 0) return;
        double top = Math.Max(20, Math.Ceiling(max * 1.25 / 10) * 10);
        using (var pen = new Pen(Pal.Grid))
        {
            g.DrawLine(pen, plot.Left, plot.Top, plot.Right, plot.Top);
            g.DrawLine(pen, plot.Left, plot.Top + plot.Height / 2, plot.Right, plot.Top + plot.Height / 2);
            g.DrawLine(pen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        }
        using (var f = new Font(Font.FontFamily, 7.5f))
        {
            TextRenderer.DrawText(g, top.ToString("0") + " ms", f, new Point((int)(plot.Right + 4 * k), (int)(plot.Top - 6 * k)), Pal.Muted, flags);
            TextRenderer.DrawText(g, "0", f, new Point((int)(plot.Right + 4 * k), (int)(plot.Bottom - 8 * k)), Pal.Muted, flags);
        }
        float bw = plot.Width / d.Length;
        for (int i = 0; i < d.Length; i++)
        {
            if (d[i] < 0) continue;
            float h = (float)Math.Max(1.5, Math.Min(1, d[i] / top) * plot.Height);
            using (var b = new SolidBrush(ColorFor(d[i])))
                g.FillRectangle(b, plot.Left + i * bw + bw * 0.15f, plot.Bottom - h, bw * 0.7f, h);
        }
    }
}

// ------------------------------------------------------------------ main window

class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string CaptureHint = "Click a button on the controller (or double-click a row), then press the key you want for it.";

    readonly Engine engine;
    Settings cfg;
    bool allowShow, loading, balloonShown;
    readonly float dpi = 1f;
    readonly NotifyIcon tray = new NotifyIcon();
    ToolStripMenuItem trayKeyboard;
    Icon icon;
    int iconState = -1;
    StatusTile tilePi, tileSwitch, tilePing, tileInput;
    PadView dashPad, mapPad;
    PingGraph graph;
    Label lblDetails, lblCapture, lblPiHint, lblSlots, lblHealthNote, lblReplies, lblDashHeading, lblConsoleHint;
    readonly Dictionary<string, Label> healthValues = new Dictionary<string, Label>();
    Button btnSelftest, btnRestart, btnReboot;
    ListView mapList;
    CheckBox chkKeyboard, chkBlock, chkRemote, chkStartup, chkMinimized, chkLowPower;
    NumericUpDown numWalk, numTrigger, numDeadzone;
    TextBox txtPi, txtLog, txtUser;
    ComboBox cmbPad, cmbLayout, cmbConsole;
    TabControl tabs;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly ToolTip tips = new ToolTip();
    string selected = "A", capturingFor;
    int captureSlot, ticks;
    long logSeen;
    double findUntil = -1;
    byte[] shownReport;

    public MainForm(Engine engine, EventWaitHandle showEvent, bool startHidden)
    {
        this.engine = engine;
        cfg = engine.Cfg;
        allowShow = !startHidden;
        Text = "SwitchPad";
        Font = new Font("Segoe UI", 9f);
        BackColor = Pal.Back;
        ForeColor = Pal.Text;
        using (var g = CreateGraphics()) dpi = g.DpiX / 96f;
        ClientSize = new Size(S(960), S(640));
        MinimumSize = new Size(S(800), S(560));
        StartPosition = FormStartPosition.CenterScreen;
        tips.AutoPopDelay = 15000;

        BuildUi();
        LoadValues();
        SetupTray();

        timer.Interval = 40;
        timer.Tick += (s, e) => Tick();
        timer.Start();

        var t = new Thread(() =>
        {
            while (true)
            {
                showEvent.WaitOne();
                try { BeginInvoke((Action)ShowFromTray); } catch (Exception) { return; }
            }
        });
        t.IsBackground = true;
        t.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    int S(int px) { return (int)Math.Round(px * dpi); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dark.TitleBar(Handle);
    }

    // ---------------------------------------------------------------- layout

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(S(10)) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(88)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var tiles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, S(8)) };
        for (int i = 0; i < 4; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        tilePi = Tile("Pi");
        tileSwitch = Tile("Switch");
        tilePing = Tile("Ping");
        tileInput = Tile("Input");
        tiles.Controls.Add(tilePi, 0, 0);
        tiles.Controls.Add(tileSwitch, 1, 0);
        tiles.Controls.Add(tilePing, 2, 0);
        tiles.Controls.Add(tileInput, 3, 0);
        root.Controls.Add(tiles, 0, 0);

        tabs = new DarkTabControl { Dock = DockStyle.Fill, Padding = new Point(S(14), S(5)) };
        tabs.TabPages.Add(BuildDashboard());
        tabs.TabPages.Add(BuildKeyboard());
        tabs.TabPages.Add(BuildPi());
        tabs.TabPages.Add(BuildSettings());
        tabs.TabPages.Add(BuildLog());
        tabs.SelectedIndexChanged += (s, e) => CancelCapture(null);
        root.Controls.Add(tabs, 0, 1);
        Controls.Add(root);
        Dark.Apply(this);
        foreach (Control c in new Control[] { txtPi, txtUser, numTrigger, numDeadzone, numWalk }) PauseKeysWhileFocused(c);
    }

    // Typing a Pi address or a number must not press Switch buttons.
    readonly List<Control> pauseControls = new List<Control>();

    void PauseKeysWhileFocused(Control c)
    {
        pauseControls.Add(c);
        c.Enter += (s, e) => KeyboardHook.Paused = true;
        c.Leave += (s, e) => KeyboardHook.Paused = false;
    }

    StatusTile Tile(string title)
    {
        return new StatusTile { Title = title, Dock = DockStyle.Fill, Margin = new Padding(S(4), 0, S(4), 0), Font = Font };
    }

    TabPage Page(string title)
    {
        return new TabPage(title) { Padding = new Padding(S(10)), UseVisualStyleBackColor = true };
    }

    Label Heading(string text)
    {
        return new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, S(2), 0, S(6)) };
    }

    Label Hint(string text)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = Pal.Muted, Margin = new Padding(S(8), S(6), 0, 0), MaximumSize = new Size(S(460), 0) };
    }

    Button Btn(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(6), S(1), S(6), S(1)) };
        b.Click += click;
        return b;
    }

    CheckBox Check(string text, string tip)
    {
        var c = new DarkCheckBox { Text = text, AutoSize = true, Margin = new Padding(0, S(4), S(18), S(4)) };
        if (tip != null) tips.SetToolTip(c, tip);
        return c;
    }

    NumericUpDown Num(int min, int max)
    {
        return new NumericUpDown { Minimum = min, Maximum = max, Width = S(70), Margin = new Padding(0, S(3), 0, S(3)) };
    }

    ComboBox Combo(params string[] items)
    {
        var c = new DarkComboBox { Width = S(270), Margin = new Padding(0, S(3), 0, S(3)) };
        c.Items.AddRange(items);
        return c;
    }

    static FlowLayoutPanel Flow(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        f.Controls.AddRange(items);
        return f;
    }

    TabPage BuildDashboard()
    {
        var page = Page("Dashboard");
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, S(10), 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lblDashHeading = Heading("What the Switch receives");
        left.Controls.Add(lblDashHeading, 0, 0);
        dashPad = new PadView { Dock = DockStyle.Fill, Margin = new Padding(0) };
        left.Controls.Add(dashPad, 0, 1);
        grid.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        right.Controls.Add(Heading("Ping to the Pi, last 60 seconds"), 0, 0);
        graph = new PingGraph { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, S(10)), Font = Font };
        right.Controls.Add(graph, 0, 1);
        right.Controls.Add(Heading("Details"), 0, 2);
        lblDetails = new Label { Dock = DockStyle.Fill, AutoSize = false, ForeColor = Pal.Text };
        right.Controls.Add(lblDetails, 0, 3);
        grid.Controls.Add(right, 1, 0);

        page.Controls.Add(grid);
        return page;
    }

    TabPage BuildKeyboard()
    {
        var page = Page("Keyboard");
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        chkKeyboard = Check("Use the keyboard", "Keys (from Parsec, or typed on this PC) drive the console as mapped below.");
        chkBlock = Check("Hide mapped keys from other programs",
            "Mapped keys only go to the console, not also to whichever window has focus on this PC.\n" +
            "Careful: while this is on, those keys don't type anywhere on this PC.");
        chkRemote = Check("Only keys sent by programs (Parsec)",
            "Ignore keys typed on this PC's own keyboard; use only keys that a program like Parsec sends.\n" +
            "Before turning this on, press a key through Parsec and check the Dashboard: it should say 'sent by a program'.");
        numWalk = Num(10, 100);
        var walkLbl = new Label { Text = "Walk key tilt (%):", AutoSize = true, Margin = new Padding(S(4), S(7), S(4), 0) };
        tips.SetToolTip(numWalk, "How far the sticks tilt while the Walk key is held (default 50%).");
        var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, S(8)) };
        opts.Controls.AddRange(new Control[] { chkKeyboard, chkBlock, chkRemote, walkLbl, numWalk });
        outer.Controls.Add(opts, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        mapPad = new PadView { Dock = DockStyle.Fill, Mapping = true, Margin = new Padding(0, 0, S(10), 0) };
        mapPad.TargetClicked += id => { SelectTarget(id); BeginCapture(0); };
        body.Controls.Add(mapPad, 0, 0);

        var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mapList = new DarkListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        mapList.Columns.Add("Switch", S(170));
        mapList.Columns.Add("Key", S(100));
        mapList.Columns.Add("2nd key", S(100));
        foreach (var t in Targets.All) mapList.Items.Add(new ListViewItem(new[] { t.LabelFor(cfg.Ps3), "", "" }) { Tag = t.Id });
        mapList.SelectedIndexChanged += (s, e) =>
        {
            if (mapList.SelectedItems.Count == 0) return;
            selected = (string)mapList.SelectedItems[0].Tag;
            mapPad.Selected = selected;
            mapPad.Invalidate();
        };
        mapList.DoubleClick += (s, e) => BeginCapture(0);
        side.Controls.Add(mapList, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
        buttons.Controls.Add(Btn("Set key", (s, e) => BeginCapture(0)));
        buttons.Controls.Add(Btn("Add 2nd key", (s, e) => BeginCapture(1)));
        buttons.Controls.Add(Btn("Clear", (s, e) => ClearSelected()));
        buttons.Controls.Add(Btn("Reset all to defaults", (s, e) => ResetKeys()));
        side.Controls.Add(buttons, 0, 1);
        body.Controls.Add(side, 1, 0);
        outer.Controls.Add(body, 0, 1);

        lblCapture = new Label
        {
            Text = CaptureHint, AutoSize = true, ForeColor = Pal.Accent,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold), Margin = new Padding(0, S(10), 0, S(2)),
        };
        outer.Controls.Add(lblCapture, 0, 2);
        page.Controls.Add(outer);

        chkKeyboard.CheckedChanged += (s, e) => Change(x => x.Keyboard = chkKeyboard.Checked);
        chkBlock.CheckedChanged += (s, e) => Change(x => x.BlockKeys = chkBlock.Checked);
        chkRemote.CheckedChanged += (s, e) => Change(x => x.RemoteKeysOnly = chkRemote.Checked);
        numWalk.ValueChanged += (s, e) => Change(x => x.WalkPercent = (int)numWalk.Value);
        return page;
    }

    static readonly string[][] HealthRows =
    {
        new[] { "model", "Model" },
        new[] { "sw", "SwitchPad on the Pi" },
        new[] { "net", "Network" },
        new[] { "temp", "CPU temperature" },
        new[] { "power", "Power" },
        new[] { "usb", "USB to the console" },
        new[] { "console", "Console mode" },
        new[] { "lowpower", "Low-power mode" },
        new[] { "up", "Running for" },
        new[] { "packets", "Packets from this PC" },
        new[] { "problems", "Hiccups and errors" },
        new[] { "load", "CPU load" },
        new[] { "selftest", "Last self-test" },
    };

    TabPage BuildPi()
    {
        var page = Page("Pi");
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, S(16), 0) };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;
        AddSpan(left, ref row, Heading("Health"));
        foreach (var hr in HealthRows)
        {
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(new Label { Text = hr[1], AutoSize = true, ForeColor = Pal.Muted, Margin = new Padding(0, S(4), S(14), S(4)) }, 0, row);
            var v = new Label { Text = "–", AutoSize = true, Margin = new Padding(0, S(4), 0, S(4)), MaximumSize = new Size(S(300), 0) };
            healthValues[hr[0]] = v;
            left.Controls.Add(v, 1, row);
            row++;
        }
        lblHealthNote = Hint("");
        lblHealthNote.Margin = new Padding(0, S(10), 0, 0);
        lblHealthNote.MaximumSize = new Size(S(420), 0);
        AddSpan(left, ref row, lblHealthNote);
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(new Label(), 0, row);
        grid.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row = 0;
        AddSpan(right, ref row, Heading("Controls"));
        btnSelftest = ActionRow(right, ref row, "Run self-test",
            "The Pi plays the test pattern by itself (12 s) and checks that the console takes every report. " +
            "Tests the Pi and its cable without the network. Your input pauses meanwhile.", (s, e) => engine.Command("selftest"));
        btnRestart = ActionRow(right, ref row, "Restart SwitchPad", "Restarts the program on the Pi. The controller is gone for about 5 s.",
            (s, e) => { if (Confirm("Restart SwitchPad on the Pi? The controller is gone for about 5 seconds.")) engine.Command("restart"); });
        btnReboot = ActionRow(right, ref row, "Reboot Pi", "Restarts the whole Pi. The controller is gone for about a minute.",
            (s, e) => { if (Confirm("Reboot the Pi? The controller is gone for about a minute.")) engine.Command("reboot"); });

        var ssh = Heading("Over SSH (asks for the Pi's password)");
        ssh.Margin = new Padding(0, S(16), 0, S(6));
        AddSpan(right, ref row, ssh);
        txtUser = new TextBox { Width = S(220), Margin = new Padding(0, S(3), 0, S(3)) };
        txtUser.Leave += (s, e) => SaveUser();
        txtUser.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveUser(); } };
        txtUser.HandleCreated += (s, e) => SendMessage(txtUser.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, "as chosen in Raspberry Pi Imager");
        tips.SetToolTip(txtUser, "The user name you chose for the Pi in Raspberry Pi Imager (step 1 of SETUP.md).");
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(new Label { Text = "Pi user name", AutoSize = true, Margin = new Padding(0, S(6), S(10), 0) }, 0, row);
        right.Controls.Add(txtUser, 1, row);
        row++;
        ActionRow(right, ref row, "Collect logs…",
            "Opens a window running collect-logs.cmd: pings the Pi, gets its report and log, and zips them with this PC's logs.",
            (s, e) => RunScript("collect-logs.cmd"));
        ActionRow(right, ref row, "Update Pi…",
            "Opens a window running update-pi.cmd: copies the pi folder from this project onto the Pi and restarts SwitchPad there.",
            (s, e) => RunScript("update-pi.cmd"));

        var answers = Heading("Answers from the Pi");
        answers.Margin = new Padding(0, S(16), 0, S(6));
        AddSpan(right, ref row, answers);
        // the answers fill the rest of the column, newest first
        lblReplies = new Label { Dock = DockStyle.Fill, AutoSize = false, ForeColor = Pal.Text, Text = "Nothing sent yet." };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(lblReplies, 0, row);
        right.SetColumnSpan(lblReplies, 2);
        grid.Controls.Add(right, 1, 0);

        page.Controls.Add(grid);
        return page;
    }

    void AddSpan(TableLayoutPanel t, ref int row, Control c)
    {
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(c, 0, row);
        t.SetColumnSpan(c, 2);
        row++;
    }

    Button ActionRow(TableLayoutPanel t, ref int row, string text, string hint, EventHandler click)
    {
        var b = new Button { Text = text, Width = S(160), Height = S(30), Margin = new Padding(0, S(4), S(10), S(4)) };
        b.Click += click;
        var h = Hint(hint);
        h.Margin = new Padding(0, S(4), 0, S(4));
        h.MaximumSize = new Size(S(290), 0);
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(b, 0, row);
        t.Controls.Add(h, 1, row);
        row++;
        return b;
    }

    bool Confirm(string question)
    {
        return MessageBox.Show(this, question, "SwitchPad", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    void SaveUser()
    {
        string u = txtUser.Text.Trim();
        if (u != cfg.PiUser) Change(x => x.PiUser = u);
    }

    static readonly System.Text.RegularExpressions.Regex SafeName = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9._-]+$");

    // collect-logs.cmd / update-pi.cmd in their own console window: they ask for the Pi's password there.
    void RunScript(string name)
    {
        SaveUser();
        string user = cfg.PiUser;
        var sn = engine.Snap;
        string host = sn.Address.Length > 0 ? sn.Address : cfg.Pi;
        if (!SafeName.IsMatch(user))
        {
            MessageBox.Show(this, "Enter the Pi's user name first (the one you chose in Raspberry Pi Imager).", "SwitchPad",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            txtUser.Focus();
            return;
        }
        if (!SafeName.IsMatch(host))
        {
            MessageBox.Show(this, "The Pi's address '" + host + "' doesn't look right. Check it under Settings.", "SwitchPad",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string path = Path.Combine(dir, name);
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "Can't find " + path + ".\nThis button needs SwitchPad.exe to stay in the project's windows folder.",
                "SwitchPad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + path + "\" " + user + "@" + host + "\"");
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = true;
            Process.Start(psi);
            Log.Write("Started " + name + " " + user + "@" + host + " in its own window");
        }
        catch (Exception e) { Log.Write("Could not start " + name + ": " + e.Message); }
    }

    static string Dur(double seconds)
    {
        if (double.IsNaN(seconds)) return "–";
        long s = (long)seconds;
        if (s < 60) return s + " s";
        if (s < 3600) return (s / 60) + " min";
        if (s < 86400) return (s / 3600) + " h " + (s % 3600 / 60) + " min";
        return (s / 86400) + " d " + (s % 86400 / 3600) + " h";
    }

    static string Count(PiHealth h, string key)
    {
        double v = h.Num(key);
        return double.IsNaN(v) ? "?" : v.ToString("N0", CultureInfo.CurrentCulture);
    }

    void SetHealth(string key, string text, Color color)
    {
        var l = healthValues[key];
        if (l.Text != text) l.Text = text;
        if (l.ForeColor != color) l.ForeColor = color;
    }

    void UpdatePiTab(Snapshot sn)
    {
        var h = sn.Health;
        bool fresh = h != null && Clock.Ms - h.ReceivedMs < 10000;
        btnSelftest.Enabled = btnRestart.Enabled = btnReboot.Enabled = sn.Online;

        string note;
        if (!sn.Online)
            note = "The Pi isn't answering." + (h != null ? " These values are from " + Ago(Clock.Ms - h.ReceivedMs) + "." : "");
        else if (!fresh && sn.OnlineSinceMs >= 0 && Clock.Ms - sn.OnlineSinceMs > 6000)
            note = "The Pi answers, but sends no health information: its SwitchPad software is older than this PC's. Use Update Pi.";
        else if (!fresh) note = "Waiting for the Pi's health information…";
        else note = "Updated " + Ago(Clock.Ms - h.ReceivedMs) + (h["host"].Length > 0 ? ". Host name: " + h["host"] : "") + ".";
        if (lblHealthNote.Text != note) lblHealthNote.Text = note;

        var newestFirst = (string[])sn.CommandLines.Clone();
        Array.Reverse(newestFirst);
        string replies = newestFirst.Length == 0 ? "Nothing sent yet." : string.Join("\n", newestFirst);
        if (lblReplies.Text != replies) lblReplies.Text = replies;

        if (h == null)
        {
            foreach (var hr in HealthRows) SetHealth(hr[0], "–", Pal.Idle);
            return;
        }
        bool live = fresh && sn.Online;
        Func<Color, Color> c = col => live ? col : Pal.Idle;

        SetHealth("model", h["model"], c(Pal.Text));
        SetHealth("sw", h["sw"], c(Pal.Text));
        string net = h["link_text"] + (h["dev"].Length > 0 ? "  (" + h["dev"] + ")" : "");
        bool netBad = h["link"] == "none" || h["link_text"].Contains("NO CABLE") || h["link_text"].Contains("NOT CONNECTED");
        SetHealth("net", net, c(netBad ? Pal.Bad : Pal.Text));

        double t = h.Num("temp");
        if (double.IsNaN(t)) SetHealth("temp", "–", c(Pal.Idle));
        else SetHealth("temp", t.ToString("0.0", CultureInfo.CurrentCulture) + " °C" +
                               (t >= 80 ? "  (too hot: the Pi slows itself down)" : t >= 70 ? "  (warm)" : ""),
                       c(t >= 80 ? Pal.Bad : t >= 70 ? Pal.Warn : Pal.Good));

        Color pc;
        SetHealth("power", PowerText(h, out pc), c(pc));
        Color uc;
        SetHealth("usb", UsbText(h["usb"], h["usb_speed"], out uc), c(uc));
        string mode = h["console"], ps = h["ps_button"];
        if (mode.Length == 0)
            SetHealth("console", cfg.Ps3 ? "This Pi's SwitchPad has no PS3 mode yet: use Update Pi" : "Switch (older SwitchPad)",
                c(cfg.Ps3 ? Pal.Bad : Pal.Text));
        else if (mode != cfg.ConsoleId)
            SetHealth("console", (mode == "ps3" ? "PS3" : "Switch") + ", switching to " + cfg.ConsoleName + "…", c(Pal.Warn));
        else if (mode == "ps3")
            SetHealth("console", ps == "ok" ? "PS3 (PS button works)" : "PS3 (PS button: " + (ps.Length > 0 ? ps : "?") + ")",
                c(ps == "ok" ? Pal.Good : Pal.Warn));
        else SetHealth("console", "Switch", c(Pal.Text));
        string low = h["lowpower"];
        if (low.Length == 0)
            SetHealth("lowpower", cfg.PiLowPower ? "This Pi's SwitchPad has no low-power mode yet: use Update Pi" : "–",
                c(cfg.PiLowPower ? Pal.Bad : Pal.Idle));
        else if ((low == "on") != cfg.PiLowPower)
            SetHealth("lowpower", "Switching " + (cfg.PiLowPower ? "on" : "off") + "…", c(Pal.Warn));
        else if (low == "on") SetHealth("lowpower", "On: " + h["lowpower_text"], c(Pal.Text));
        else SetHealth("lowpower", "Off", c(Pal.Text));
        SetHealth("up", "Pi " + Dur(h.Num("uptime")) + "   ·   SwitchPad " + Dur(h.Num("running")), c(Pal.Text));
        SetHealth("packets", Count(h, "rx") + " received, " + Count(h, "lost") + " lost, " + Count(h, "late") + " out of order", c(Pal.Text));
        bool clean = h.Num("hiccups") == 0 && h.Num("failsafe") == 0 && h.Num("usb_err") == 0;
        SetHealth("problems", Count(h, "hiccups") + " network gaps over 150 ms, " + Count(h, "failsafe") + " failsafe releases, " +
                              Count(h, "usb_err") + " USB write errors", c(clean ? Pal.Good : Pal.Warn));
        SetHealth("load", h["load"].Length > 0 ? h["load"] : "–", c(Pal.Text));
        string st = h["selftest"];
        SetHealth("selftest", st.Length == 0 ? "not run since SwitchPad started on the Pi" : st == "running" ? "running…" : st,
            c(st.Contains("FAILED") ? Pal.Bad : st.Contains("OK") ? Pal.Good : Pal.Text));
    }

    // get_throttled bits: 0 under-voltage now, 1 frequency capped now, 2 throttled now, 3 soft
    // temperature limit now; 16-19 the same "since boot".
    static string PowerText(PiHealth h, out Color color)
    {
        long f = h.PowerFlags;
        if (f < 0) { color = Pal.Idle; return h["power_text"].Length > 0 ? h["power_text"] : "–"; }
        const string fix = " The Pi may restart by itself. Use a powered USB hub, another port, or Settings → Low-power mode.";
        if ((f & 0x1) != 0) { color = Pal.Bad; return "UNDER-VOLTAGE NOW: the USB port, power supply or cable is too weak." + fix; }
        if ((f & 0xE) != 0) { color = Pal.Warn; return "Slowed down now (too hot or low voltage)"; }
        if ((f & 0x10000) != 0) { color = Pal.Warn; return "OK now, but there was under-voltage since the Pi started." + fix; }
        if ((f & 0xE0000) != 0) { color = Pal.Warn; return "OK now, but it was slowed down since the Pi started"; }
        color = Pal.Good;
        return "OK";
    }

    static string UsbText(string state, string speed, out Color color)
    {
        string s;
        switch (state)
        {
            case "configured": color = Pal.Good; s = "In use by the console"; break;
            case "not attached": color = Pal.Bad; s = "Not connected (cable, or console off / Switch not docked?)"; break;
            case "suspended": color = Pal.Warn; s = "Suspended (is the console asleep?)"; break;
            case "gadget not bound": color = Pal.Bad; s = "USB controller mode isn't set up on the Pi"; break;
            case "": case "None": color = Pal.Idle; s = "–"; break;
            default: color = Pal.Warn; s = "Connecting (" + state + ")"; break;
        }
        if (speed.Length > 0 && speed != "UNKNOWN" && state == "configured") s += ", " + speed;
        return s;
    }

    TabPage BuildSettings()
    {
        var page = Page("Settings");
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;

        txtPi = new TextBox { Width = S(200), Margin = new Padding(0, S(4), S(6), S(4)) };
        txtPi.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyPi(); } };
        lblPiHint = Hint("Name or IP address of the Pi. The IP address connects faster.");
        AddRow(t, ref row, "Pi address", Flow(txtPi, Btn("Apply", (s, e) => ApplyPi()), Btn("Find Pi", (s, e) => FindPi())), lblPiHint);

        cmbPad = Combo("Auto (highest connected slot)", "Slot 0", "Slot 1", "Slot 2", "Slot 3");
        lblSlots = Hint("");
        AddRow(t, ref row, "Controller", cmbPad, lblSlots);

        cmbConsole = Combo("Nintendo Switch", "PlayStation 3");
        lblConsoleHint = Hint("");
        AddRow(t, ref row, "Console", cmbConsole, lblConsoleHint);

        chkLowPower = Check("Low-power mode", null);
        AddRow(t, ref row, "Pi power", chkLowPower, Hint(
            "For a Pi 4 that restarts by itself on a weak USB port (a PS3, some dock ports): the Pi runs " +
            "its CPU at the lowest speed, turns its LEDs off, its Wi-Fi radio off while Ethernet is used, " +
            "and Ethernet at 100 Mbit/s. Starting up still needs full power: a powered USB hub is the sure fix."));

        cmbLayout = Combo("Same position (Xbox A → Switch B)", "Same letter (Xbox A → Switch A)");
        AddRow(t, ref row, "Button layout", cmbLayout, Hint("Which Switch button each Xbox face button presses. " +
            "On a PS3 it is always by position: Xbox A → Cross, B → Circle, X → Square, Y → Triangle."));

        numTrigger = Num(1, 254);
        AddRow(t, ref row, "Trigger point", numTrigger, Hint("How far LT / RT must be pulled to press ZL / ZR (L2 / R2 on a PS3), out of 255. Default 30."));

        numDeadzone = Num(0, 50);
        AddRow(t, ref row, "Stick deadzone (%)", numDeadzone, Hint("Ignores small stick movements around the centre (against stick drift). Default 0 = off."));

        chkStartup = Check("Start SwitchPad when I sign in to Windows", null);
        AddRow(t, ref row, "Startup", chkStartup, null);
        chkMinimized = Check("Start minimized to the tray", null);
        AddRow(t, ref row, "", chkMinimized, null);

        AddRow(t, ref row, "Test", Btn("Send test pattern (12 s)", (s, e) => engine.StartTest()),
            Hint("Presses L+R (L1+R1 on a PS3) three times, D-pad right and left, then circles the left stick. " +
                 "To register the controller, open Controllers > Change Grip/Order on the Switch first."));
        AddRow(t, ref row, "Files", Btn("Open logs folder", (s, e) => OpenFolder(Log.Dir)),
            Hint("Settings are saved in " + Settings.FilePath));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(new Label(), 0, row);
        page.Controls.Add(t);

        cmbPad.SelectedIndexChanged += (s, e) => Change(x => x.Pad = cmbPad.SelectedIndex - 1);
        cmbConsole.SelectedIndexChanged += (s, e) => Change(x => x.Ps3 = cmbConsole.SelectedIndex == 1);
        chkLowPower.CheckedChanged += (s, e) => Change(x => x.PiLowPower = chkLowPower.Checked);
        cmbLayout.SelectedIndexChanged += (s, e) => Change(x => x.Positional = cmbLayout.SelectedIndex == 0);
        numTrigger.ValueChanged += (s, e) => Change(x => x.TriggerThreshold = (int)numTrigger.Value);
        numDeadzone.ValueChanged += (s, e) => Change(x => x.Deadzone = (int)numDeadzone.Value);
        chkMinimized.CheckedChanged += (s, e) => Change(x => x.StartMinimized = chkMinimized.Checked);
        chkStartup.CheckedChanged += (s, e) => { if (!loading) SetStartup(chkStartup.Checked); };
        return page;
    }

    void AddRow(TableLayoutPanel t, ref int row, string label, Control control, Control hint)
    {
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, S(7), S(14), S(4)), Font = new Font(Font, FontStyle.Bold) }, 0, row);
        t.Controls.Add(control, 1, row);
        if (hint != null) t.Controls.Add(hint, 2, row);
        row++;
    }

    TabPage BuildLog()
    {
        var page = Page("Log");
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        txtLog = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Dock = DockStyle.Fill, Font = new Font("Consolas", 9f),
        };
        t.Controls.Add(txtLog, 0, 0);
        var bottom = Flow(Btn("Open logs folder", (s, e) => OpenFolder(Log.Dir)), Hint("Everything here is also in " + Log.FilePath));
        bottom.Margin = new Padding(0, S(6), 0, 0);
        t.Controls.Add(bottom, 0, 1);
        page.Controls.Add(t);
        return page;
    }

    void LoadValues()
    {
        loading = true;
        txtPi.Text = cfg.Pi;
        txtUser.Text = cfg.PiUser;
        cmbPad.SelectedIndex = cfg.Pad + 1;
        cmbConsole.SelectedIndex = cfg.Ps3 ? 1 : 0;
        chkLowPower.Checked = cfg.PiLowPower;
        cmbLayout.SelectedIndex = cfg.Positional ? 0 : 1;
        numTrigger.Value = cfg.TriggerThreshold;
        numDeadzone.Value = cfg.Deadzone;
        numWalk.Value = cfg.WalkPercent;
        chkKeyboard.Checked = cfg.Keyboard;
        chkBlock.Checked = cfg.BlockKeys;
        chkRemote.Checked = cfg.RemoteKeysOnly;
        chkMinimized.Checked = cfg.StartMinimized;
        chkStartup.Checked = StartupEnabled();
        loading = false;
        RefreshKeys();
        SelectTarget(selected);
    }

    // ---------------------------------------------------------------- settings changes

    void Change(Action<Settings> edit)
    {
        if (loading) return;
        var s = cfg.Clone();
        edit(s);
        Commit(s);
    }

    // Every change: a new Settings object to the engine, saved to settings.ini right away.
    void Commit(Settings s)
    {
        cfg = s;
        engine.Apply(s);
        s.Save();
        loading = true;
        chkKeyboard.Checked = s.Keyboard;
        loading = false;
        if (trayKeyboard != null) trayKeyboard.Checked = s.Keyboard;
        RefreshKeys();
    }

    void RefreshKeys()
    {
        chkBlock.Enabled = chkRemote.Enabled = numWalk.Enabled = cfg.Keyboard;
        ApplyConsoleLabels();
        mapList.BeginUpdate();
        foreach (ListViewItem it in mapList.Items)
        {
            var t = Targets.Find((string)it.Tag);
            if (t != null && it.Text != t.LabelFor(cfg.Ps3)) it.Text = t.LabelFor(cfg.Ps3);
            int[] k = cfg.Keys((string)it.Tag);
            it.SubItems[1].Text = k[0] != 0 ? KeyNames.Pretty(k[0]) : "–";
            it.SubItems[2].Text = k[1] != 0 ? KeyNames.Pretty(k[1]) : "";
        }
        mapList.EndUpdate();
        foreach (var p in new[] { mapPad, dashPad }) { p.Cfg = cfg; p.ResetTip(); p.Invalidate(); }
    }

    // Texts that name the console (Switch or PS3).
    void ApplyConsoleLabels()
    {
        string name = cfg.ConsoleName;
        lblDashHeading.Text = "What the " + name + " receives";
        if (tileSwitch.Title != name) { tileSwitch.Title = name; tileSwitch.Invalidate(); }
        if (mapList.Columns[0].Text != name) mapList.Columns[0].Text = name;
        cmbLayout.Enabled = !cfg.Ps3;
        lblConsoleHint.Text = cfg.Ps3
            ? "The Pi acts as a PS3 pad: Guide (or Back+Start) is the PS button, Back / Start are Select / Start. " +
              "No motion, rumble or pressure-sensitive buttons. Changing this reconnects the controller for a second."
            : "The Pi acts as a wired Switch pad (HORI Pokken style). Changing this reconnects the controller for a second.";
    }

    void SelectTarget(string id)
    {
        selected = id;
        foreach (ListViewItem it in mapList.Items)
        {
            if ((string)it.Tag != id) continue;
            it.Selected = true;
            it.Focused = true;
            it.EnsureVisible();
        }
        mapPad.Selected = id;
        mapPad.Invalidate();
    }

    void BeginCapture(int slot)
    {
        if (selected == null) return;
        if (!KeyboardHook.Running)
        {
            lblCapture.Text = "Keys can't be read: the keyboard hook isn't running (see the Log tab).";
            lblCapture.ForeColor = Pal.Bad;
            return;
        }
        capturingFor = selected;
        captureSlot = slot;
        var t = Targets.Find(selected);
        lblCapture.Text = "Press a key for " + t.LabelFor(cfg.Ps3) + (slot == 1 ? " (2nd key)" : "") + "…   Esc cancels.";
        lblCapture.ForeColor = Pal.Select;
        KeyboardHook.BeginCapture(vk =>
        {
            try { BeginInvoke((Action)(() => OnCaptured(vk))); } catch (Exception) { }
        });
    }

    void CancelCapture(string message)
    {
        if (capturingFor == null) return;
        KeyboardHook.CancelCapture();
        capturingFor = null;
        lblCapture.Text = message ?? CaptureHint;
        lblCapture.ForeColor = Pal.Accent;
    }

    void OnCaptured(int vk)
    {
        string id = capturingFor;
        if (id == null) return;
        capturingFor = null;
        lblCapture.ForeColor = Pal.Accent;
        if (vk == 0x1B) { lblCapture.Text = "Cancelled. " + CaptureHint; return; }

        var s = cfg.Clone();
        string takenFrom = null;
        foreach (var kv in s.KeyMap)
        {
            for (int i = 0; i < kv.Value.Length; i++)
            {
                if (kv.Value[i] != vk || (kv.Key == id && i == captureSlot)) continue;
                kv.Value[i] = 0;
                if (kv.Key != id) takenFrom = kv.Key;
            }
        }
        int[] keys = s.Keys(id);
        if (captureSlot == 1 && keys[0] == 0) keys[0] = vk;
        else keys[captureSlot] = vk;
        if (keys[0] == 0 && keys[1] != 0) { keys[0] = keys[1]; keys[1] = 0; }
        s.KeyMap[id] = keys;
        Commit(s);

        var t = Targets.Find(id);
        string msg = KeyNames.Pretty(vk) + " → " + t.LabelFor(cfg.Ps3);
        if (takenFrom != null) msg += "   (" + KeyNames.Pretty(vk) + " no longer does " + Targets.Find(takenFrom).LabelFor(cfg.Ps3) + ")";
        lblCapture.Text = msg;
        Log.Write("Key map: " + t.Label + " = " + KeyNames.ListPretty(keys) +
                  (takenFrom != null ? " (taken from " + Targets.Find(takenFrom).Label + ")" : ""));
    }

    void ClearSelected()
    {
        if (selected == null) return;
        CancelCapture(null);
        var s = cfg.Clone();
        s.KeyMap[selected] = new int[2];
        Commit(s);
        lblCapture.Text = "No key for " + Targets.Find(selected).LabelFor(cfg.Ps3) + " now.";
    }

    void ResetKeys()
    {
        CancelCapture(null);
        if (MessageBox.Show(this, "Set all keys back to the defaults?", "SwitchPad", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        var s = cfg.Clone();
        s.KeyMap = Targets.DefaultMap();
        Commit(s);
        Log.Write("Key map: reset to the defaults");
        lblCapture.Text = "All keys are back to the defaults.";
    }

    void ApplyPi()
    {
        string p = txtPi.Text.Trim();
        if (p.Length == 0) { txtPi.Text = cfg.Pi; return; }
        if (p != cfg.Pi) Change(x => x.Pi = p);
        lblPiHint.Text = "Saved. Talking to " + p + " now.";
    }

    void FindPi()
    {
        engine.Link.Discover();
        findUntil = Clock.Ms + 3500;
        lblPiHint.Text = "Searching the local network…";
    }

    static bool StartupEnabled()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("SwitchPad") != null;
        }
        catch (Exception) { return false; }
    }

    void SetStartup(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("SwitchPad", "\"" + Application.ExecutablePath + "\" --minimized");
                else k.DeleteValue("SwitchPad", false);
            }
            Log.Write("Start with Windows: " + (on ? "on" : "off"));
        }
        catch (Exception e)
        {
            Log.Write("Could not change 'start with Windows': " + e.Message);
            loading = true;
            chkStartup.Checked = StartupEnabled();
            loading = false;
        }
    }

    static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); }
        catch (Exception e) { Log.Write("Could not open " + dir + ": " + e.Message); }
    }

    // ---------------------------------------------------------------- live updates

    static string Ms(double v) { return v.ToString("0", CultureInfo.InvariantCulture) + " ms"; }

    void Tick()
    {
        ticks++;
        var sn = engine.Snap;

        var h = sn.Health;
        bool fresh = h != null && Clock.Ms - h.ReceivedMs < 10000;
        string piSub = sn.Address + (fresh ? "  ·  " + h.LinkName : "");
        if (sn.Online)
        {
            long power = fresh ? h.PowerFlags : -1;
            double temp = fresh ? h.Num("temp") : double.NaN;
            if (power > 0 && (power & 0x1) != 0) tilePi.Set("Low power!", piSub + "  ·  see the Pi tab", Pal.Bad);
            else if (temp >= 80) tilePi.Set("Too hot", piSub + "  ·  " + temp.ToString("0") + " °C", Pal.Warn);
            else tilePi.Set("Online", piSub, Pal.Good);
        }
        else if (sn.Address.Length == 0) tilePi.Set("Looking up…", sn.Host, Pal.Warn);
        else tilePi.Set("No reply", sn.Address, Pal.Bad);

        if (!sn.Online) tileSwitch.Set("Unknown", "Waiting for the Pi", Pal.Idle);
        else if (sn.Attached) tileSwitch.Set("Connected", "The " + cfg.ConsoleName + " is using the controller", Pal.Good);
        else tileSwitch.Set("Not connected", cfg.Ps3 ? "Is the PS3 on, cable in its USB port?" : "Is the Switch docked and awake?", Pal.Bad);

        double sum = 0;
        int n = 0;
        foreach (double v in sn.PingHistory) if (v >= 0) { sum += v; n++; }
        if (sn.Online && sn.Ping >= 0)
            tilePing.Set(Ms(sn.Ping), n > 0 ? "Last minute: " + Ms(sum / n) + " average" : "", PingGraph.ColorFor(sn.Ping));
        else tilePing.Set("–", "No answers from the Pi", Pal.Idle);

        if (sn.Testing) tileInput.Set("Test pattern", "Sending the scripted test", Pal.Warn);
        else if (sn.PadSlot >= 0)
            tileInput.Set(sn.KeyboardHeld ? "Controller + keys" : "Controller",
                "XInput slot " + sn.PadSlot + (sn.Keyboard ? ", keyboard on" : ""), Pal.Good);
        else if (sn.Keyboard)
            tileInput.Set(sn.KeyboardHeld ? "Keyboard" : "Keyboard ready", "No controller connected", sn.KeyboardHeld ? Pal.Good : Pal.Idle);
        else tileInput.Set("Nothing", "No controller, keyboard off", Pal.Bad);

        if (!Report.Same(sn.Current, shownReport))
        {
            shownReport = sn.Current;
            dashPad.State = mapPad.State = sn.Current;
            if (tabs.SelectedIndex == 0) dashPad.Invalidate();
            if (tabs.SelectedIndex == 1) mapPad.Invalidate();
        }

        if (ticks % 6 == 0)
        {
            if (graph.Data != sn.PingHistory) { graph.Data = sn.PingHistory; graph.Invalidate(); }
            string details = Details(sn);
            if (lblDetails.Text != details) lblDetails.Text = details;
            string slots = "Connected now: " + (sn.Slots == "none" ? "none" : "slot " + sn.Slots.Replace(",", ", "));
            if (lblSlots.Text != slots) lblSlots.Text = slots;
            UpdatePiTab(sn);
            UpdateTray(sn);
        }

        var lines = Log.Since(ref logSeen);
        if (lines.Count > 0)
        {
            if (txtLog.TextLength > 300000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 150000);
            txtLog.AppendText(string.Join("\r\n", lines.ToArray()) + "\r\n");
        }

        string found = engine.Link.TakeFound();
        if (found != null)
        {
            findUntil = -1;
            txtPi.Text = found;
            if (found != cfg.Pi) Change(x => x.Pi = found);
            lblPiHint.Text = "Found the Pi at " + found + " and saved it.";
        }
        else if (findUntil > 0 && Clock.Ms > findUntil)
        {
            findUntil = -1;
            lblPiHint.Text = "No answer. Is the Pi switched on and on the same network as this PC?";
        }
    }

    string Details(Snapshot sn)
    {
        var lines = new List<string>();
        string sending = Report.Describe(sn.Current, cfg.Ps3);
        lines.Add("Sending:  " + (sending == "-" ? "nothing pressed" : sending));
        lines.Add("Controller:  " + (sn.PadSlot >= 0 ? "XInput slot " + sn.PadSlot : "none") +
                  "   (connected slots: " + sn.Slots + ")");
        string key = "Keyboard:  " + (sn.Keyboard ? "on" : "off");
        double ago = Clock.Ms - KeyboardHook.LastKeyMs;
        if (KeyboardHook.LastVk == 0) key += "   ·   no key pressed yet";
        else if (ago < 3600000)
            key += "   ·   last key " + KeyNames.Pretty(KeyboardHook.LastVk) + ", " +
                   (KeyboardHook.LastInjected ? "sent by a program (Parsec)" : "typed on this PC") + ", " + Ago(ago);
        lines.Add(key);
        lines.Add("Network:  " + sn.SentPerSec + " packets/s to the Pi, " + sn.RepliesPerSec + "/s answers back");
        var h = sn.Health;
        string pi = "Pi:  " + sn.LinkText;
        if (h != null && Clock.Ms - h.ReceivedMs < 10000)
            pi += ", " + h["link_text"] + (h["temp"].Length > 0 ? ", CPU " + h["temp"] + " °C" : "");
        lines.Add(pi);
        return string.Join("\n\n", lines.ToArray());
    }

    static string Ago(double ms)
    {
        if (ms < 1500) return "just now";
        if (ms < 90000) return (int)(ms / 1000) + " s ago";
        return (int)(ms / 60000) + " min ago";
    }

    // ---------------------------------------------------------------- tray icon

    void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open SwitchPad", null, (s, e) => ShowFromTray());
        trayKeyboard = new ToolStripMenuItem("Use the keyboard", null, (s, e) => Change(x => x.Keyboard = !x.Keyboard));
        trayKeyboard.Checked = cfg.Keyboard;
        menu.Items.Add(trayKeyboard);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit SwitchPad", null, (s, e) => Close());
        Dark.Menu(menu);
        tray.ContextMenuStrip = menu;
        tray.Text = "SwitchPad";
        tray.DoubleClick += (s, e) => ShowFromTray();
        UpdateTray(engine.Snap);
        tray.Visible = true;
    }

    void UpdateTray(Snapshot sn)
    {
        int state = !sn.Online ? 0 : sn.Attached ? 2 : 1;
        string text = "SwitchPad: " + (!sn.Online ? "Pi not answering" : cfg.ConsoleName + (sn.Attached ? " connected" : " not connected")) +
                      (sn.Online && sn.Ping >= 0 ? ", " + Ms(sn.Ping) : "");
        if (text.Length > 63) text = text.Substring(0, 63);
        if (tray.Text != text) tray.Text = text;
        if (state == iconState) return;
        iconState = state;
        Icon old = icon;
        icon = MakeIcon(state == 2 ? Pal.Good : state == 1 ? Pal.Warn : Pal.Bad);
        tray.Icon = icon;
        Icon = icon;
        if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
    }

    // A small dark gamepad with a status light: green = all good, yellow = Pi ok but no Switch, red = no Pi.
    static Icon MakeIcon(Color status)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(0x2B, 0x31, 0x37)))
                using (var p = Gfx.Rounded(new RectangleF(1, 6, 30, 20), 9))
                    g.FillPath(b, p);
                using (var b = new SolidBrush(Color.FromArgb(0xE6, 0xE9, 0xEC)))
                {
                    g.FillRectangle(b, 5, 14.5f, 9, 3);
                    g.FillRectangle(b, 8, 11.5f, 3, 9);
                }
                using (var b = new SolidBrush(status)) g.FillEllipse(b, 17, 10, 12, 12);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    void ShowFromTray()
    {
        allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != FormWindowState.Minimized || !tray.Visible) return;
        Hide();
        if (!balloonShown)
        {
            balloonShown = true;
            tray.ShowBalloonTip(3000, "SwitchPad is still running", "Double-click the icon here to open it again.", ToolTipIcon.Info);
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // (also called while the window is still being built)
        var a = ActiveControl;
        KeyboardHook.Paused = a != null && (pauseControls.Contains(a) || pauseControls.Contains(a.Parent));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        KeyboardHook.Paused = false;
        CancelCapture("Cancelled (SwitchPad lost focus). " + CaptureHint);
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Key releases don't arrive while the screen is locked: forget what was held.
        if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect)
            KeyboardHook.ReleaseAll();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        timer.Stop();
        KeyboardHook.CancelCapture();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        tray.Visible = false;
    }
}
#endif
