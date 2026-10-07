// Idle Sleep - puts the PC to sleep after a set time with no mouse, keyboard
// or game controller use, even if programs (videos, games, downloads) are open.
// Build with build.bat (uses the C# compiler that ships with Windows).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mutex = new Mutex(true, "IdleSleep_SingleInstance_7f3a", out created))
        {
            if (!created)
            {
                MessageBox.Show("Idle Sleep is already running.\nLook for the moon icon near the clock.", "Idle Sleep");
                return;
            }
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool startHidden = args.Length > 0 && args[0].Equals("/tray", StringComparison.OrdinalIgnoreCase);
            Application.Run(new MainForm(startHidden));
        }
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("kernel32.dll")] public static extern uint GetTickCount();
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool LockWorkStation();

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons; public byte bLeftTrigger; public byte bRightTrigger;
        public short sThumbLX; public short sThumbLY; public short sThumbRX; public short sThumbRY;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint index, out XINPUT_STATE state);

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr retLen);

    // Sleep needs the shutdown privilege switched on for this process.
    public static void EnableShutdownPrivilege()
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token)) return;
        try
        {
            var tp = new TOKEN_PRIVILEGES { Count = 1, Attributes = 0x2 };
            if (LookupPrivilegeValue(null, "SeShutdownPrivilege", out tp.Luid))
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }
}

// Records the last mouse use and the last keyboard use separately.
// Runs on its own thread so the hooks keep working while the window is busy.
static class InputWatcher
{
    public static volatile uint LastMouse, LastKey;
    static readonly Native.HookProc mouseProc = OnMouse, keyProc = OnKey; // kept alive for the hooks

    public static void Start()
    {
        LastMouse = LastKey = Native.GetTickCount();
        var t = new Thread(Run) { IsBackground = true, Name = "InputWatcher" };
        t.Start();
    }

    static void Run()
    {
        IntPtr mod = Native.GetModuleHandle(null);
        Native.SetWindowsHookEx(14 /* WH_MOUSE_LL */, mouseProc, mod, 0);
        Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, keyProc, mod, 0);
        Native.MSG msg;
        while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
    }

    static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0) LastMouse = Native.GetTickCount();
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0) LastKey = Native.GetTickCount();
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
}

// Big rounded on/off button with an animated rainbow (RGB) colour. Off = "TURN ON", on = "ON".
class BigToggle : Control
{
    bool isChecked, hover, down;
    float hue;
    System.Windows.Forms.Timer anim;
    public event EventHandler CheckedChanged;

    public BigToggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        float scale;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        Size = new Size((int)(380 * scale), (int)(64 * scale));
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        anim = new System.Windows.Forms.Timer { Interval = 40 };
        anim.Tick += delegate { hue = (hue + 0.8f) % 360; Invalidate(); };
    }

    public bool Checked
    {
        get { return isChecked; }
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            UpdateAnim();
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }
    }

    // Only animate while the window is actually showing.
    void UpdateAnim() { anim.Enabled = Visible; }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateAnim(); }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool inside = ClientRectangle.Contains(e.Location);
        down = false;
        Invalidate();
        base.OnMouseUp(e);
        if (inside && e.Button == MouseButtons.Left) Checked = !Checked;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent != null ? Parent.BackColor : SystemColors.Control);

        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        int radius = Math.Max(10, Height / 4);
        using (var path = RoundRect(r, radius))
        {
            // Always a moving rainbow (RGB); the text says which state it is in.
            string text = isChecked ? "✓  ON  (click to turn off)" : "TURN ON";
            using (var br = new LinearGradientBrush(r, Color.Red, Color.Red, LinearGradientMode.Horizontal))
            {
                var blend = new ColorBlend(7);
                blend.Positions = new float[] { 0f, 1f / 6, 2f / 6, 3f / 6, 4f / 6, 5f / 6, 1f };
                var cols = new Color[7];
                // Soft colours that only span part of the rainbow at a time, so it is calm rather than flashy.
                for (int i = 0; i < 7; i++) cols[i] = FromHsv(hue + i * 25, 0.5, 0.85);
                blend.Colors = cols;
                br.InterpolationColors = blend;
                g.FillPath(br, path);
            }
            if (hover || down)
                using (var tint = new SolidBrush(down ? Color.FromArgb(50, 0, 0, 0) : Color.FromArgb(35, 255, 255, 255)))
                    g.FillPath(tint, path);

            using (var pen = new Pen(Color.FromArgb(70, 0, 0, 0), 1.5f)) g.DrawPath(pen, path);

            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var shadowRect = new RectangleF(r.X + 1, r.Y + 2, r.Width, r.Height);
            using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.DrawString(text, Font, shadow, shadowRect, fmt);
            using (var white = new SolidBrush(Color.White)) g.DrawString(text, Font, white, r, fmt);
            if (Focused)
                using (var pen = new Pen(Color.White, 1f) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, r.X + 5, r.Y + 5, r.Width - 10, r.Height - 10);
        }
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c, r, gr, b;
        if (h < 60) { r = c; gr = x; b = 0; }
        else if (h < 120) { r = x; gr = c; b = 0; }
        else if (h < 180) { r = 0; gr = c; b = x; }
        else if (h < 240) { r = 0; gr = x; b = c; }
        else if (h < 300) { r = x; gr = 0; b = c; }
        else { r = c; gr = 0; b = x; }
        return Color.FromArgb((int)((r + m) * 255), (int)((gr + m) * 255), (int)((b + m) * 255));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && anim != null) anim.Dispose();
        base.Dispose(disposing);
    }
}

class Settings
{
    public bool Enabled = false;
    public int Minutes = 30;
    // What to do when idle: 0 Sleep, 1 Hibernate, 2 Shut down, 3 Restart, 4 Lock.
    public int Action = 0;
    // For Shut down / Restart: close open programs even if they have unsaved work.
    public bool ForceClose = false;
    // Instead of a fixed time, pick a random time between 1 and 5 hours (and don't show it).
    public bool RandomTime = false;
    public int RandomMinMinutes = 60;  // shortest random wait (1 hour)
    public int RandomMaxMinutes = 300; // longest random wait (5 hours)
    // Warning and beep are off by default: usually the person is asleep.
    public bool Warn = false;
    public bool Beep = false;
    public bool CountMouse = true;
    public bool CountKeyboard = true;
    public bool CountController = true;

    static string FilePath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IdleSleep-settings.txt"); }
    }

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                int n;
                if (key == "Enabled") s.Enabled = val == "1";
                else if (key == "Minutes" && int.TryParse(val, out n)) s.Minutes = Math.Max(1, Math.Min(720, n));
                else if (key == "Action" && int.TryParse(val, out n)) s.Action = Math.Max(0, Math.Min(4, n));
                else if (key == "ForceClose") s.ForceClose = val == "1";
                else if (key == "RandomTime") s.RandomTime = val == "1";
                else if (key == "RandomMinMinutes" && int.TryParse(val, out n)) s.RandomMinMinutes = Math.Max(30, Math.Min(1440, n));
                else if (key == "RandomMaxMinutes" && int.TryParse(val, out n)) s.RandomMaxMinutes = Math.Max(30, Math.Min(1440, n));
                else if (key == "Warn") s.Warn = val == "1";
                else if (key == "Beep") s.Beep = val == "1";
                else if (key == "CountMouse") s.CountMouse = val == "1";
                else if (key == "CountKeyboard") s.CountKeyboard = val == "1";
                else if (key == "CountController") s.CountController = val == "1";
            }
            if (s.RandomMaxMinutes < s.RandomMinMinutes) s.RandomMaxMinutes = s.RandomMinMinutes;
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllLines(FilePath, new[] {
                "Enabled=" + (Enabled ? "1" : "0"),
                "Minutes=" + Minutes,
                "Action=" + Action,
                "ForceClose=" + (ForceClose ? "1" : "0"),
                "RandomTime=" + (RandomTime ? "1" : "0"),
                "RandomMinMinutes=" + RandomMinMinutes,
                "RandomMaxMinutes=" + RandomMaxMinutes,
                "Warn=" + (Warn ? "1" : "0"),
                "Beep=" + (Beep ? "1" : "0"),
                "CountMouse=" + (CountMouse ? "1" : "0"),
                "CountKeyboard=" + (CountKeyboard ? "1" : "0"),
                "CountController=" + (CountController ? "1" : "0"),
            });
        }
        catch { }
    }
}

class WarningForm : Form
{
    Label lblTime;
    public event Action StayAwake;
    public string Phrase = "fall asleep"; // what the PC is about to do

    public WarningForm(Icon icon)
    {
        Text = "Idle Sleep";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-3000, -3000);
        Font = new Font("Segoe UI", 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
        };
        lblTime = new Label { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold) };
        var hint = new Label { AutoSize = true, Text = "Move the mouse to stop it, or click the button.", Margin = new Padding(3, 4, 3, 8) };
        var btn = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        btn.Click += delegate { FireStayAwake(); };
        panel.Controls.Add(lblTime);
        panel.Controls.Add(hint);
        panel.Controls.Add(btn);
        Controls.Add(panel);
        SetSeconds(60);
    }

    // Show on top of other windows without stealing focus from a game.
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x8; /* WS_EX_TOPMOST */ return cp; }
    }

    void FireStayAwake() { if (StayAwake != null) StayAwake(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; FireStayAwake(); }
        base.OnFormClosing(e);
    }

    public void SetSeconds(int secs)
    {
        lblTime.Text = secs >= 60
            ? "This PC will " + Phrase + " in 1 minute"
            : string.Format("This PC will {0} in {1} second{2}", Phrase, secs, secs == 1 ? "" : "s");
    }

    public void PlaceBottomRight()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 20, wa.Bottom - Height - 20);
    }
}

class MainForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "IdleSleep";

    Settings settings = Settings.Load();
    NotifyIcon tray;
    System.Windows.Forms.Timer timer;
    WarningForm warning;
    BigToggle chkOn;
    CheckBox chkWarn, chkBeep, chkMouse, chkKeys, chkPad, chkStartup, chkForce;
    RadioButton[] radAction;
    Button btnNow;
    NumericUpDown numMin;
    CheckBox chkRandom;
    FlowLayoutPanel timeRow, randRow;
    NumericUpDown numRandMin, numRandMax; // random range, in hours
    Random rng = new Random();
    uint randomLimitMs, lastIdleMs; // the hidden random wait, and last tick's idle time
    Label lblStatus;
    ToolStripMenuItem menuToggle, menuNow;

    static readonly string[] ActionNames = { "Sleep", "Hibernate", "Shut down", "Restart", "Lock" };
    // Fits "This PC will ___ in 1 minute".
    static readonly string[] ActionPhrases = { "fall asleep", "hibernate", "shut down", "restart", "lock" };
    Icon iconOn, iconOff;

    bool startHidden, reallyExit, sleeping, shownHideTip, loading, beeped;
    uint armedTick, lastPadTick;
    Native.XINPUT_GAMEPAD[] padPrev = new Native.XINPUT_GAMEPAD[4];
    bool[] padSeen = new bool[4];
    bool padApiOk = true;

    public MainForm(bool startHidden)
    {
        this.startHidden = startHidden;
        // Opened by hand: always start turned off. Started by Windows (/tray): start turned on.
        settings.Enabled = startHidden;
        iconOn = MakeMoonIcon(true);
        iconOff = MakeMoonIcon(false);

        Text = "Idle Sleep";
        Icon = iconOn;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildUi();
        BuildTray();

        loading = true;
        chkOn.Checked = settings.Enabled;
        numMin.Value = settings.Minutes;
        radAction[settings.Action].Checked = true;
        chkForce.Checked = settings.ForceClose;
        numRandMin.Value = settings.RandomMinMinutes / 60m;
        numRandMax.Value = settings.RandomMaxMinutes / 60m;
        chkRandom.Checked = settings.RandomTime;
        chkWarn.Checked = settings.Warn;
        chkBeep.Checked = settings.Beep;
        chkMouse.Checked = settings.CountMouse;
        chkKeys.Checked = settings.CountKeyboard;
        chkPad.Checked = settings.CountController;
        chkStartup.Checked = GetStartup();
        loading = false;
        UpdateActionText();

        ResetIdle();
        InputWatcher.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += delegate { OnTick(); };
        timer.Start();
        OnTick();
    }

    void BuildUi()
    {
        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16),
        };

        root.Controls.Add(new Label
        {
            AutoSize = true, Margin = new Padding(3, 0, 3, 12),
            Text = "Does something to the PC when nobody uses it for a while\n(sleep, hibernate, shut down, restart or lock). Programs can stay open.",
        });

        chkOn = new BigToggle { Margin = new Padding(3, 0, 3, 12) };
        chkOn.CheckedChanged += delegate { if (!loading && chkOn.Checked) ResetIdle(); Apply(); };
        root.Controls.Add(chkOn);

        root.Controls.Add(new Label { Text = "When nobody uses the PC, do this (pick one):", AutoSize = true, Margin = new Padding(3, 0, 3, 2) });
        // Radio buttons in one panel = only one can be chosen.
        var actionRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(19, 0, 0, 4) };
        radAction = new RadioButton[ActionNames.Length];
        for (int i = 0; i < ActionNames.Length; i++)
        {
            var rb = new RadioButton { Text = ActionNames[i], AutoSize = true, Margin = new Padding(3, 3, 12, 3) };
            rb.CheckedChanged += delegate(object s, EventArgs ev) { if (((RadioButton)s).Checked) { UpdateActionText(); Apply(); } };
            radAction[i] = rb;
            actionRow.Controls.Add(rb);
        }
        root.Controls.Add(actionRow);

        chkForce = new CheckBox { Text = "Force programs to close (unsaved work is lost)", AutoSize = true, Margin = new Padding(22, 2, 3, 2) };
        chkForce.CheckedChanged += delegate { Apply(); };
        root.Controls.Add(chkForce);

        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
        row.Controls.Add(new Label { Text = "After", AutoSize = true, Anchor = AnchorStyles.Left });
        numMin = new NumericUpDown { Minimum = 1, Maximum = 720, Width = 80, Anchor = AnchorStyles.Left };
        numMin.ValueChanged += delegate { Apply(); };
        row.Controls.Add(numMin);
        row.Controls.Add(new Label { Text = "minutes of no use", AutoSize = true, Anchor = AnchorStyles.Left });
        root.Controls.Add(row);
        timeRow = row;

        chkRandom = new CheckBox { Text = "Use a random time instead (not shown)", AutoSize = true };
        chkRandom.CheckedChanged += delegate { timeRow.Enabled = !chkRandom.Checked; randRow.Enabled = chkRandom.Checked; Apply(); };
        root.Controls.Add(chkRandom);

        randRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Enabled = false, Margin = new Padding(19, 0, 0, 4) };
        randRow.Controls.Add(new Label { Text = "Between", AutoSize = true, Anchor = AnchorStyles.Left });
        numRandMin = new NumericUpDown { DecimalPlaces = 1, Increment = 0.5m, Minimum = 0.5m, Maximum = 24m, Width = 65, Anchor = AnchorStyles.Left };
        numRandMax = new NumericUpDown { DecimalPlaces = 1, Increment = 0.5m, Minimum = 0.5m, Maximum = 24m, Width = 65, Anchor = AnchorStyles.Left };
        // Keep the shortest from going above the longest.
        numRandMin.ValueChanged += delegate { if (!loading && numRandMin.Value > numRandMax.Value) numRandMax.Value = numRandMin.Value; Apply(); };
        numRandMax.ValueChanged += delegate { if (!loading && numRandMax.Value < numRandMin.Value) numRandMin.Value = numRandMax.Value; Apply(); };
        randRow.Controls.Add(numRandMin);
        randRow.Controls.Add(new Label { Text = "and", AutoSize = true, Anchor = AnchorStyles.Left });
        randRow.Controls.Add(numRandMax);
        randRow.Controls.Add(new Label { Text = "hours", AutoSize = true, Anchor = AnchorStyles.Left });
        root.Controls.Add(randRow);

        chkWarn =new CheckBox { Text = "Show a little warning 1 minute before (with a Cancel button)", AutoSize = true };
        chkWarn.CheckedChanged += delegate { Apply(); };
        root.Controls.Add(chkWarn);

        chkBeep = new CheckBox { Text = "Beep 1 minute before", AutoSize = true };
        chkBeep.CheckedChanged += delegate { Apply(); };
        root.Controls.Add(chkBeep);

        root.Controls.Add(new Label { Text = "What counts as using the PC:", AutoSize = true, Margin = new Padding(3, 10, 3, 2) });
        var indent = new Padding(22, 2, 3, 2);
        chkMouse = new CheckBox { Text = "Mouse", AutoSize = true, Margin = indent };
        chkMouse.CheckedChanged += delegate { Apply(); };
        root.Controls.Add(chkMouse);
        chkKeys = new CheckBox { Text = "Keyboard", AutoSize = true, Margin = indent };
        chkKeys.CheckedChanged += delegate { Apply(); };
        root.Controls.Add(chkKeys);
        chkPad = new CheckBox { Text = "Game controller (Xbox-style)", AutoSize = true, Margin = new Padding(22, 2, 3, 10) };
        chkPad.CheckedChanged += delegate { Apply(); };
        root.Controls.Add(chkPad);

        chkStartup = new CheckBox { Text = "Start automatically when Windows starts", AutoSize = true };
        chkStartup.CheckedChanged += delegate { if (!loading) SetStartup(chkStartup.Checked); };
        root.Controls.Add(chkStartup);

        lblStatus = new Label { AutoSize = true, Margin = new Padding(3, 14, 3, 14), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        root.Controls.Add(lblStatus);

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        btnNow = MakeButton("Sleep now", delegate { NowClicked("Now button"); });
        buttons.Controls.Add(btnNow);
        buttons.Controls.Add(MakeButton("Hide", delegate { HideToTray(); }));
        buttons.Controls.Add(MakeButton("Exit", delegate { ExitApp(); }));
        root.Controls.Add(buttons);

        root.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 12, 3, 0),
            Text = "Closing this window keeps Idle Sleep running (moon icon by the clock).\nUse Exit to stop it completely.",
        });

        Controls.Add(root);
    }

    static Button MakeButton(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        b.Click += click;
        return b;
    }

    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Idle Sleep", null, delegate { ShowFromTray(); });
        menuToggle = new ToolStripMenuItem("Turn off", null, delegate { chkOn.Checked = !chkOn.Checked; });
        menu.Items.Add(menuToggle);
        menuNow = new ToolStripMenuItem("Sleep now", null, delegate { NowClicked("Now (tray menu)"); });
        menu.Items.Add(menuNow);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { ExitApp(); });

        tray = new NotifyIcon { Icon = iconOn, Text = "Idle Sleep", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { ShowFromTray(); };
    }

    // Start hidden (only the tray icon) when launched with /tray at Windows startup.
    protected override void SetVisibleCore(bool value)
    {
        if (startHidden)
        {
            startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    void Apply()
    {
        if (loading) return;
        settings.Enabled = chkOn.Checked;
        settings.Minutes = (int)numMin.Value;
        settings.Action = GetAction();
        settings.ForceClose = chkForce.Checked;
        settings.RandomTime = chkRandom.Checked;
        int rMin = (int)Math.Round(numRandMin.Value * 60), rMax = (int)Math.Round(numRandMax.Value * 60);
        bool rangeChanged = rMin != settings.RandomMinMinutes || rMax != settings.RandomMaxMinutes;
        settings.RandomMinMinutes = rMin;
        settings.RandomMaxMinutes = rMax;
        if (rangeChanged) PickRandom();
        settings.Warn = chkWarn.Checked;
        settings.Beep = chkBeep.Checked;
        settings.CountMouse = chkMouse.Checked;
        settings.CountKeyboard = chkKeys.Checked;
        settings.CountController = chkPad.Checked;
        settings.Save();
        OnTick();
    }

    void ResetIdle()
    {
        armedTick = Native.GetTickCount();
        lastPadTick = armedTick;
        lastIdleMs = 0;
        PickRandom();
    }

    // A new hidden wait somewhere in the chosen range (to the second), used only when Random time is ticked.
    void PickRandom()
    {
        int lo = settings.RandomMinMinutes * 60;
        int hi = Math.Max(lo, settings.RandomMaxMinutes * 60);
        randomLimitMs = (uint)rng.Next(lo, hi + 1) * 1000u;
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // The tick counter keeps running during sleep, so start counting fresh after waking.
        if (e.Mode == PowerModes.Resume)
        {
            ResetIdle();
            Log("Woke up");
        }
    }

    void OnTick()
    {
        if (timer == null || sleeping) return;

        var lii = new Native.LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);
        bool gotInput = Native.GetLastInputInfo(ref lii);
        uint now = Native.GetTickCount();

        uint idleMs = now - armedTick;
        if (settings.CountMouse && settings.CountKeyboard)
        {
            // Both ticked: use Windows' own "last input" time (the most reliable).
            if (gotInput) idleMs = Math.Min(idleMs, Since(now, lii.dwTime));
        }
        else
        {
            if (settings.CountMouse) idleMs = Math.Min(idleMs, Since(now, InputWatcher.LastMouse));
            if (settings.CountKeyboard) idleMs = Math.Min(idleMs, Since(now, InputWatcher.LastKey));
        }
        if (settings.CountController)
        {
            PollControllers(now);
            idleMs = Math.Min(idleMs, now - lastPadTick);
        }

        if (!settings.Enabled)
        {
            HideWarning();
            lblStatus.Text = "Turned off. Nothing will happen to the PC.";
            SetTray(false, "Idle Sleep: off");
            return;
        }

        // With Random time, every new stretch of no use gets its own fresh hidden wait.
        if (idleMs < lastIdleMs) PickRandom();
        lastIdleMs = idleMs;

        uint limitMs = settings.RandomTime ? randomLimitMs : (uint)settings.Minutes * 60000u;
        uint warnMs = (settings.Warn || settings.Beep) ? Math.Min(60000u, limitMs / 2) : 0;
        uint leftMs = idleMs >= limitMs ? 0 : limitMs - idleMs;

        if (leftMs == 0)
        {
            RunAction(settings.RandomTime ? "no use for a random time" : string.Format("no use for {0} min", settings.Minutes));
            return;
        }

        if (warnMs > 0 && leftMs <= warnMs) ShowWarning(leftMs);
        else HideWarning();

        if (settings.RandomTime)
        {
            // Don't reveal how long is left.
            lblStatus.Text = string.Format("Not used for {0}.  Random time: it will happen later.", Fmt(idleMs));
            SetTray(true, "Idle Sleep: on (random time)");
            return;
        }
        if (!settings.CountMouse && !settings.CountKeyboard && !settings.CountController)
            lblStatus.Text = string.Format("Nothing is ticked, so it will {0} in {1} no matter what.", ActionPhrases[settings.Action], Fmt(leftMs));
        else
            lblStatus.Text = string.Format("Not used for {0}.  Will {1} in {2}.", Fmt(idleMs), ActionPhrases[settings.Action], Fmt(leftMs));
        SetTray(true, "Idle Sleep: " + ActionNames[settings.Action].ToLower() + " in " + FmtShort(leftMs));
    }

    void PollControllers(uint now)
    {
        if (!padApiOk) return;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                Native.XINPUT_STATE st;
                if (Native.XInputGetState(i, out st) != 0) { padSeen[i] = false; continue; }
                var g = st.Gamepad;
                if (padSeen[i] && PadActive(padPrev[i], g)) lastPadTick = now;
                padPrev[i] = g;
                padSeen[i] = true;
            }
        }
        catch { padApiOk = false; }
    }

    // Ignore tiny stick drift so a resting controller does not keep the PC awake.
    static bool PadActive(Native.XINPUT_GAMEPAD a, Native.XINPUT_GAMEPAD b)
    {
        if (a.wButtons != b.wButtons) return true;
        if (Math.Abs(a.bLeftTrigger - b.bLeftTrigger) > 30 || Math.Abs(a.bRightTrigger - b.bRightTrigger) > 30) return true;
        return StickMoved(a.sThumbLX, b.sThumbLX) || StickMoved(a.sThumbLY, b.sThumbLY)
            || StickMoved(a.sThumbRX, b.sThumbRX) || StickMoved(a.sThumbRY, b.sThumbRY);
    }

    static bool StickMoved(short before, short after)
    {
        return Math.Abs((int)after) > 8000 || Math.Abs(after - before) > 4000;
    }

    // Last minute before sleep: beep once and/or show the little warning, each only if ticked.
    void ShowWarning(uint leftMs)
    {
        if (settings.Beep && !beeped)
        {
            beeped = true;
            SystemSounds.Exclamation.Play();
        }
        if (!settings.Warn)
        {
            if (warning != null && warning.Visible) warning.Hide();
            return;
        }
        if (warning == null)
        {
            warning = new WarningForm(iconOn);
            warning.StayAwake += delegate { ResetIdle(); HideWarning(); OnTick(); };
        }
        warning.Phrase = ActionPhrases[settings.Action];
        warning.SetSeconds((int)Math.Ceiling(leftMs / 1000.0));
        if (!warning.Visible) warning.Show();
        warning.PlaceBottomRight();
    }

    void HideWarning()
    {
        beeped = false;
        if (warning != null && warning.Visible) warning.Hide();
    }

    // Button / tray "now": ask first for the actions that can lose work.
    void NowClicked(string reason)
    {
        int a = settings.Action;
        if ((a == 2 || a == 3) && MessageBox.Show(ActionNames[a] + " the PC right now?", "Idle Sleep",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        RunAction(reason);
    }

    int GetAction()
    {
        for (int i = 0; i < radAction.Length; i++) if (radAction[i].Checked) return i;
        return 0;
    }

    void UpdateActionText()
    {
        int a = GetAction();
        btnNow.Text = ActionNames[a] + " now";
        menuNow.Text = ActionNames[a] + " now";
        chkForce.Visible = a == 2 || a == 3;
    }

    // Does the chosen action: 0 Sleep, 1 Hibernate, 2 Shut down, 3 Restart, 4 Lock.
    void RunAction(string reason)
    {
        if (sleeping) return;
        sleeping = true;
        HideWarning();
        int a = settings.Action;
        Log(ActionNames[a] + " (" + reason + ")");
        Native.EnableShutdownPrivilege();
        uint force = settings.ForceClose ? 0x4u : 0u; // EWX_FORCE
        bool ok;
        switch (a)
        {
            case 1: ok = Native.SetSuspendState(true, false, false); break;
            case 2: ok = Native.ExitWindowsEx(0x8u | force, 0x80000000u); break; // EWX_POWEROFF
            case 3: ok = Native.ExitWindowsEx(0x2u | force, 0x80000000u); break; // EWX_REBOOT
            case 4: ok = Native.LockWorkStation(); break;
            default: ok = Native.SetSuspendState(false, false, false); break;
        }
        int err = Marshal.GetLastWin32Error();
        if (!ok)
        {
            Log("Windows refused: " + ActionNames[a] + ", error " + err);
            tray.ShowBalloonTip(6000, "Idle Sleep", "Windows would not " + ActionPhrases[a] + " (error " + err + ").", ToolTipIcon.Warning);
        }
        ResetIdle();
        sleeping = false;
    }

    void SetTray(bool on, string text)
    {
        tray.Icon = on ? iconOn : iconOff;
        tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        menuToggle.Text = on ? "Turn off" : "Turn on";
    }

    void HideToTray()
    {
        Hide();
        if (!shownHideTip)
        {
            shownHideTip = true;
            tray.ShowBalloonTip(5000, "Idle Sleep is still running",
                "Find the moon icon by the clock (it may be under the ^ arrow). Right-click it for options.", ToolTipIcon.Info);
        }
    }

    void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        reallyExit = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        if (warning != null) warning.Dispose();
        base.OnFormClosed(e);
    }

    static bool GetStartup()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }
        catch { return false; }
    }

    static void SetStartup(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" /tray");
                else k.DeleteValue(RunName, false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not change the startup setting:\n" + ex.Message, "Idle Sleep");
        }
    }

    static readonly object logLock = new object();

    static void Log(string msg)
    {
        try
        {
            lock (logLock)
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IdleSleep-log.txt");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 200000) fi.Delete();
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
        }
        catch { }
    }

    // Milliseconds since a tick count, or 0 if that tick is (just) in the future.
    static uint Since(uint now, uint tick)
    {
        return (int)(now - tick) >= 0 ? now - tick : 0;
    }

    static string Fmt(uint ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalHours >= 1) return string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        return string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
    }

    static string FmtShort(uint ms)
    {
        if (ms < 60000) return (int)Math.Ceiling(ms / 1000.0) + " sec";
        return (int)Math.Ceiling(ms / 60000.0) + " min";
    }

    static Icon MakeMoonIcon(bool on)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(on ? Color.FromArgb(255, 205, 80) : Color.FromArgb(150, 150, 150)))
                    g.FillEllipse(b, 2, 2, 28, 28);
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var cut = new SolidBrush(Color.Transparent))
                    g.FillEllipse(cut, 11, -3, 25, 25);
            }
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            Native.DestroyIcon(h);
            return icon;
        }
    }
}
