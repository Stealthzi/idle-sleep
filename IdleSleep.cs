// Idle Sleep - puts the PC to sleep after a set time with no mouse, keyboard
// or game controller use, even if programs (videos, games, downloads) are open.
// Build with build.bat (uses the C# compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Idle Sleep")]
[assembly: AssemblyProduct("Idle Sleep")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]

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
                // Already running: ask that copy to open its window (it may have no tray icon to click).
                EventWaitHandle show;
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out show))
                {
                    Native.AllowSetForegroundWindow(-1); // let the running copy come to the front
                    show.Set();
                    show.Dispose();
                }
                else MessageBox.Show("Idle Sleep is already running.\nLook for the moon icon near the clock.", "Idle Sleep");
                return;
            }
            try { Native.SetProcessDPIAware(); } catch { }
            // Idle Sleep runs as administrator, so Windows blocks the "taskbar is ready" message that Explorer
            // sends to normal programs. Let it through, so the moon icon comes back if Idle Sleep started before
            // the taskbar was ready (at log on) or if Explorer restarts.
            try { Native.ChangeWindowMessageFilter(Native.RegisterWindowMessage("TaskbarCreated"), 1 /* MSGFLT_ADD */); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool startHidden = args.Length > 0 && args[0].Equals("/tray", StringComparison.OrdinalIgnoreCase);
            var form = new MainForm(startHidden);
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
            {
                var waiter = new Thread(delegate ()
                {
                    while (showEvent.WaitOne())
                    {
                        try { form.BeginInvoke((Action)form.ShowFromTray); } catch { }
                    }
                }) { IsBackground = true, Name = "ShowWaiter" };
                waiter.Start();
                Application.Run(form);
            }
        }
    }

    // A second copy sets this to make the running copy open its window.
    const string ShowEventName = "IdleSleep_ShowWindow_7f3a";
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilter(uint msg, uint flag);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
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

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll")]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int len, out int retLen);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr RegisterPowerSettingNotification(IntPtr hwnd, ref Guid setting, int flags);
    [DllImport("user32.dll")]
    public static extern IntPtr CreateCursor(IntPtr inst, int x, int y, int w, int h, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")] public static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);

    // A 32x32 mouse pointer that is fully see-through.
    public static IntPtr BlankCursor()
    {
        var and = new byte[128];
        for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
        return CreateCursor(GetModuleHandle(null), 0, 0, 32, 32, and, new byte[128]);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public int l, t, r, b; public IntPtr lParam; }
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);

    // Windows' own "automatically hide the taskbar" setting.
    public static bool TaskbarAutoHide
    {
        get
        {
            var d = new APPBARDATA();
            d.cbSize = (uint)Marshal.SizeOf(d);
            d.hWnd = FindWindow("Shell_TrayWnd", null);
            return ((ulong)SHAppBarMessage(4 /* ABM_GETSTATE */, ref d) & 1) != 0;
        }
        set
        {
            var d = new APPBARDATA();
            d.cbSize = (uint)Marshal.SizeOf(d);
            d.hWnd = FindWindow("Shell_TrayWnd", null);
            d.lParam = (IntPtr)((value ? 1 /* ABS_AUTOHIDE */ : 0) | 2 /* ABS_ALWAYSONTOP */);
            SHAppBarMessage(10 /* ABM_SETSTATE */, ref d);
        }
    }

    // True if the process runs as administrator. A process that cannot even be looked at counts as yes.
    public static bool IsElevated(IntPtr process)
    {
        IntPtr tok;
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out tok)) return true;
        try
        {
            int elevated, len;
            if (!GetTokenInformation(tok, 20 /* TokenElevation */, out elevated, 4, out len)) return true;
            return elevated != 0;
        }
        finally { CloseHandle(tok); }
    }
}

// Hides the mouse pointer everywhere by swapping every Windows pointer for a see-through one,
// and puts the person's own pointers back. Restore() is also called at start in case an earlier run
// was stopped while the pointer was hidden.
static class PointerHider
{
    static readonly uint[] Ids = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };
    public static bool Hidden;

    public static void Hide()
    {
        if (Hidden) return;
        Hidden = true;
        try { foreach (uint id in Ids) Native.SetSystemCursor(Native.BlankCursor(), id); } catch { }
    }

    public static void Show() { if (Hidden) Restore(); }

    public static void Restore()
    {
        Hidden = false;
        try { Native.SystemParametersInfo(0x57 /* SPI_SETCURSORS */, 0, IntPtr.Zero, 0); } catch { }
    }
}

// Finds which programs are connected to another computer over the internet (or local network).
// A WoW with no such connection is sitting at the login screen (never logged in, or disconnected);
// in the game, at character select and in a queue it is always connected to the game server.
static class Net
{
    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    public static HashSet<int> PidsOnline()
    {
        var set = new HashSet<int>();
        Read(2, set);   // IPv4
        Read(23, set);  // IPv6
        return set;
    }

    static void Read(int af, HashSet<int> set)
    {
        bool v6 = af == 23;
        int rowSize = v6 ? 56 : 24, size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 5 /* TCP_TABLE_OWNER_PID_ALL */, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            size += 4096; // room for connections opened in the meantime
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                uint r = GetExtendedTcpTable(buf, ref size, false, af, 5, 0);
                if (r == 122 /* buffer too small */) continue;
                if (r != 0) return;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    IntPtr row = buf + 4 + i * rowSize;
                    int state = Marshal.ReadInt32(row, v6 ? 48 : 0);
                    if (state != 5 /* ESTABLISHED */) continue;
                    if (IsLocal(row, v6)) continue;
                    set.Add(Marshal.ReadInt32(row, v6 ? 52 : 20));
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    // True if the far end is this PC itself (127.x.x.x, ::1) or blank.
    static bool IsLocal(IntPtr row, bool v6)
    {
        if (!v6)
        {
            uint addr = (uint)Marshal.ReadInt32(row, 12);
            return addr == 0 || (addr & 0xFF) == 127;
        }
        var b = new byte[16];
        Marshal.Copy(row + 24, b, 0, 16);
        bool zero10 = true;
        for (int i = 0; i < 10; i++) if (b[i] != 0) zero10 = false;
        if (!zero10) return false;
        if (b[10] == 0xFF && b[11] == 0xFF) return b[12] == 127 || (b[12] | b[13] | b[14] | b[15]) == 0; // IPv4 inside IPv6
        if (b[10] == 0 && b[11] == 0 && b[12] == 0 && b[13] == 0 && b[14] == 0) return b[15] <= 1; // :: or ::1
        return false;
    }
}

// Idle Sleep's own night light: warmer (less blue) colours on every screen, made with each screen's
// colour curve in the graphics card. Windows 11 26H2 no longer lets programs switch Windows' own Night light.
static class NightFilter
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string output, IntPtr mode);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);

    // Each screen's own curve from before Idle Sleep warmed it (kept in the night light file too, so a
    // sudden stop can still be put right), and the curve Idle Sleep last set on it.
    public static readonly Dictionary<string, ushort[]> Bases = new Dictionary<string, ushort[]>();
    static readonly Dictionary<string, ushort[]> applied = new Dictionary<string, ushort[]>();
    static readonly Dictionary<string, double> caps = new Dictionary<string, double>(); // warmest a screen allowed
    public static double Level;        // 0 = normal colours, 1 = the chosen strength (it fades in between)
    public static int Strength = 80;   // 0 to 100 (100 = as warm as Windows allows)
    public static bool Limited;        // Windows would not let a screen go as warm as asked
    public static bool BasesChanged;   // a new normal curve was noted (the file should be saved)

    static ushort[] Read(string dev)
    {
        IntPtr dc = CreateDC(null, dev, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try { var r = new ushort[768]; return GetDeviceGammaRamp(dc, r) ? r : null; }
        finally { DeleteDC(dc); }
    }

    static bool Write(string dev, ushort[] r)
    {
        IntPtr dc = CreateDC(null, dev, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try { return SetDeviceGammaRamp(dc, r); }
        finally { DeleteDC(dc); }
    }

    // Close enough (graphics drivers may round the numbers a little).
    static bool Same(ushort[] a, ushort[] b)
    {
        if (a == null || b == null) return false;
        for (int i = 0; i < 768; i++) if (Math.Abs(a[i] - b[i]) > 256) return false;
        return true;
    }

    // How much green and blue are kept for warm light of this strength (red stays full).
    // Strength 0 = daylight white (6500 K), 100 = 3300 K: Windows refuses curves that take away more
    // than half of a colour, and 3300 K keeps just over half of the blue.
    static double Green(double kelvin) { return 99.4708025861 * Math.Log(kelvin / 100) - 161.1195681661; }
    static double Blue(double kelvin) { return kelvin <= 1900 ? 0 : 138.5177312231 * Math.Log(kelvin / 100 - 10) - 305.0447927307; }

    static ushort[] Curve(ushort[] bas, double level)
    {
        double k = 6500 - Strength / 100.0 * (6500 - 3300);
        double g = Math.Max(0, Math.Min(1, Green(k) / Green(6500))), b = Math.Max(0, Math.Min(1, Blue(k) / Blue(6500)));
        g = 1 - level * (1 - g);
        b = 1 - level * (1 - b);
        var r = new ushort[768];
        for (int i = 0; i < 256; i++)
        {
            r[i] = bas[i];
            r[256 + i] = (ushort)Math.Round(bas[256 + i] * g);
            r[512 + i] = (ushort)Math.Round(bas[512 + i] * b);
        }
        return r;
    }

    // Puts every screen at the current Level. Cheap to call again: a screen already right is left alone,
    // and one that a game or Windows has reset gets the warmth back.
    public static void Apply()
    {
        if (Level <= 0 && applied.Count == 0) return; // never warmed: leave the screens completely alone
        bool limited = false;
        foreach (var scr in Screen.AllScreens)
        {
            string dev = scr.DeviceName;
            ushort[] cur = Read(dev), bas, last;
            if (cur == null) continue;
            bool haveBase = Bases.TryGetValue(dev, out bas), haveLast = applied.TryGetValue(dev, out last);
            // First time this run (with no saved one), or something else replaced Idle Sleep's curve:
            // what the screen shows now is its normal curve.
            if (!haveBase || (haveLast && !Same(cur, last) && !Same(cur, bas)))
            {
                bas = cur;
                Bases[dev] = cur;
                BasesChanged = true;
            }
            if (Level <= 0)
            {
                if (!Same(cur, bas)) Write(dev, bas);
                continue;
            }
            double level = Level, cap;
            if (caps.TryGetValue(dev, out cap) && level > cap) { level = cap; limited = true; }
            ushort[] want = Curve(bas, level);
            if (Same(cur, want)) { applied[dev] = want; continue; }
            // Windows refuses curves it thinks are too far from normal; then go as warm as it allows.
            while (!Write(dev, want))
            {
                level *= 0.85;
                limited = true;
                if (level < 0.05) { want = null; break; }
                want = Curve(bas, level);
            }
            if (level < Level) caps[dev] = level;
            if (want != null) applied[dev] = want;
        }
        if (Level <= 0) applied.Clear();
        Limited = limited;
    }

    // A new strength may be allowed further than the old one was.
    public static void StrengthChanged() { caps.Clear(); }

    // Normal colours on every screen Idle Sleep warmed (on Exit, and if the program is stopped).
    public static void Restore()
    {
        foreach (var kv in applied)
        {
            ushort[] bas;
            if (Bases.TryGetValue(kv.Key, out bas)) try { Write(kv.Key, bas); } catch { }
        }
        applied.Clear();
    }

    public static string ToText(ushort[] r)
    {
        var bytes = new byte[r.Length * 2];
        Buffer.BlockCopy(r, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static ushort[] FromText(string s)
    {
        try
        {
            var bytes = Convert.FromBase64String(s);
            if (bytes.Length != 1536) return null;
            var r = new ushort[768];
            Buffer.BlockCopy(bytes, 0, r, 0, bytes.Length);
            return r;
        }
        catch { return null; }
    }
}

// What Idle Sleep has learned about when you like the night light, kept in IdleSleep-nightlight.txt.
// Times are counted from the start of "your day" (the first use after 5+ hours away), never from the
// clock, so the PC's time zone does not matter. Moments are stored as UTC ticks (0 = none).
class NightPlan
{
    public long DayStart, LastUse, FirstOnAt, NoAutoOnDay, NoAutoOffDay, OnTimesDay, LengthsDay;
    public bool On, FirstOnWasAuto;
    // Minutes into your day it was turned on, and minutes it stayed on: the newest last, at most 7.
    public List<int> OnTimes = new List<int>(), Lengths = new List<int>();
    public const int Keep = 7;

    static string FilePath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IdleSleep-nightlight.txt"); }
    }

    // One per day: a second one on the same day replaces that day's.
    public void Add(List<int> list, ref long day, int minutes)
    {
        if (day == DayStart && list.Count > 0) list[list.Count - 1] = minutes;
        else
        {
            list.Add(minutes);
            while (list.Count > Keep) list.RemoveAt(0);
        }
        day = DayStart;
    }

    public static int Average(List<int> list)
    {
        if (list.Count == 0) return -1;
        long sum = 0;
        foreach (int m in list) sum += m;
        return (int)Math.Round((double)sum / list.Count);
    }

    public static NightPlan Load()
    {
        var p = new NightPlan();
        try
        {
            if (!File.Exists(FilePath)) return p;
            var bases = new Dictionary<string, ushort[]>();
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                long n;
                bool num = long.TryParse(val, out n);
                if (key == "On") p.On = val == "1";
                else if (key == "FirstOnWasAuto") p.FirstOnWasAuto = val == "1";
                else if (key == "DayStart" && num) p.DayStart = n;
                else if (key == "LastUse" && num) p.LastUse = n;
                else if (key == "FirstOnAt" && num) p.FirstOnAt = n;
                else if (key == "NoAutoOnDay" && num) p.NoAutoOnDay = n;
                else if (key == "NoAutoOffDay" && num) p.NoAutoOffDay = n;
                else if (key == "OnTimesDay" && num) p.OnTimesDay = n;
                else if (key == "LengthsDay" && num) p.LengthsDay = n;
                else if (key == "OnTimes") p.OnTimes = Numbers(val, 30, 1440);
                else if (key == "Lengths") p.Lengths = Numbers(val, 5, 1440);
                else if (key.StartsWith("Normal ")) { var r = NightFilter.FromText(val); if (r != null) bases[key.Substring(7)] = r; }
            }
            // The saved normal curves only matter if the screens may still be warm from last time.
            if (p.On) foreach (var kv in bases) NightFilter.Bases[kv.Key] = kv.Value;
        }
        catch { }
        return p;
    }

    static List<int> Numbers(string s, int min, int max)
    {
        var list = new List<int>();
        foreach (var part in s.Split(','))
        {
            int n;
            if (int.TryParse(part.Trim(), out n) && n >= min && n <= max) list.Add(n);
        }
        while (list.Count > Keep) list.RemoveAt(0);
        return list;
    }

    public void Save()
    {
        try
        {
            var lines = new List<string> {
                "On=" + (On ? "1" : "0"),
                "DayStart=" + DayStart,
                "LastUse=" + LastUse,
                "FirstOnAt=" + FirstOnAt,
                "FirstOnWasAuto=" + (FirstOnWasAuto ? "1" : "0"),
                "NoAutoOnDay=" + NoAutoOnDay,
                "NoAutoOffDay=" + NoAutoOffDay,
                "OnTimes=" + string.Join(",", OnTimes.ConvertAll(x => x.ToString()).ToArray()),
                "OnTimesDay=" + OnTimesDay,
                "Lengths=" + string.Join(",", Lengths.ConvertAll(x => x.ToString()).ToArray()),
                "LengthsDay=" + LengthsDay,
            };
            foreach (var kv in NightFilter.Bases) lines.Add("Normal " + kv.Key + "=" + NightFilter.ToText(kv.Value));
            File.WriteAllLines(FilePath, lines.ToArray());
        }
        catch { }
    }
}

// Light and dark colours, and a helper that paints a whole window with them.
static class Theme
{
    public static bool Dark;
    public static Color Back   { get { return Dark ? Color.FromArgb(22, 22, 27)    : Color.FromArgb(242, 243, 247); } }
    public static Color Card   { get { return Dark ? Color.FromArgb(40, 40, 48)    : Color.White; } }
    public static Color Text   { get { return Dark ? Color.FromArgb(248, 248, 252) : Color.FromArgb(30, 30, 36); } }
    public static Color Sub    { get { return Dark ? Color.FromArgb(185, 185, 200) : Color.FromArgb(105, 105, 118); } }
    public static Color Head   { get { return Dark ? Color.FromArgb(178, 186, 255) : Color.FromArgb(70, 80, 190); } }
    public static Color Border { get { return Dark ? Color.FromArgb(72, 72, 84)    : Color.FromArgb(220, 222, 230); } }
    public static Color Input  { get { return Dark ? Color.FromArgb(24, 24, 30)    : Color.White; } }
    public static Color Button { get { return Dark ? Color.FromArgb(58, 58, 68)    : Color.FromArgb(234, 235, 240); } }

    // Windows' own setting for apps (used until the person picks light or dark here).
    public static bool WindowsIsDark()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                return v is int && (int)v == 0;
            }
        }
        catch { return false; }
    }

    // Dark or light title bar (Windows 10 2004 and newer; older versions just ignore it).
    public static void TitleBar(Form f)
    {
        if (!f.IsHandleCreated) return;
        int v = Dark ? 1 : 0;
        try
        {
            Native.DwmSetWindowAttribute(f.Handle, 20, ref v, 4);
            Native.DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
        }
        catch { }
    }

    // Labels can be tagged "sub" (grey hint) or "head" (section heading).
    public static void Apply(Control c, Color bg)
    {
        if (c is Card) bg = Card;
        if (c is BigToggle) { c.Invalidate(); return; }
        if (c is System.Windows.Forms.Button)
        {
            var b = (System.Windows.Forms.Button)c;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Border;
            b.BackColor = Button;
            b.ForeColor = Text;
            return;
        }
        if (c is ComboBox) ((ComboBox)c).FlatStyle = FlatStyle.Flat;
        if (c is NumericUpDown || c is ComboBox)
        {
            c.BackColor = Input;
            c.ForeColor = Text;
            return;
        }
        c.BackColor = bg;
        string tag = c.Tag as string;
        c.ForeColor = tag == "sub" ? Sub : tag == "head" ? Head : Text;
        foreach (Control child in c.Controls) Apply(child, bg);
        c.Invalidate();
    }
}

// A white (or dark grey) box with a thin border that groups related options.
class Card : FlowLayoutPanel
{
    public Card()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Padding = new Padding(12, 8, 12, 10);
        Margin = new Padding(0, 0, 0, 10);
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

// Records the last mouse use and the last keyboard use separately.
// Runs on its own thread so the hooks keep working while the window is busy.
// Fake input sent by programs (games, controller mappers, anti-AFK tools) and tiny mouse
// wobbles are not counted; LastIgnored remembers when such input last came in.
static class InputWatcher
{
    public static volatile uint LastMouse, LastKey, LastIgnored;
    public static uint StartTick; // LastMouse/LastKey still equal to this = nothing seen since the start
    static readonly Native.HookProc mouseProc = OnMouse, keyProc = OnKey; // kept alive for the hooks
    static int mouseX = int.MinValue, mouseY;

    public static void Start()
    {
        LastMouse = LastKey = LastIgnored = StartTick = Native.GetTickCount();
        var t = new Thread(Run) { IsBackground = true, Name = "InputWatcher", Priority = ThreadPriority.Highest };
        t.Start();
    }

    static void Run()
    {
        IntPtr mod = Native.GetModuleHandle(null), mouseHook = IntPtr.Zero, keyHook = IntPtr.Zero;
        // Windows quietly drops these hooks if the PC is ever too busy to answer in time (a heavy game),
        // so they are put back every minute.
        Native.SetTimer(IntPtr.Zero, IntPtr.Zero, 60000, IntPtr.Zero);
        Native.MSG msg;
        do
        {
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
            mouseHook = Native.SetWindowsHookEx(14 /* WH_MOUSE_LL */, mouseProc, mod, 0);
            keyHook = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, keyProc, mod, 0);
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0 && msg.message != 0x0113 /* WM_TIMER */) { }
        } while (msg.message == 0x0113);
    }

    static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            uint now = Native.GetTickCount();
            int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
            int flags = Marshal.ReadInt32(lParam, 12);
            bool move = (int)wParam == 0x0200; // WM_MOUSEMOVE
            if ((flags & 0x3) != 0) LastIgnored = now; // sent by a program
            else if (move && mouseX != int.MinValue && Math.Abs(x - mouseX) < 3 && Math.Abs(y - mouseY) < 3) LastIgnored = now;
            else { LastMouse = now; mouseX = x; mouseY = y; }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            uint now = Native.GetTickCount();
            int flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & 0x12) != 0) LastIgnored = now; // sent by a program
            else LastKey = now;
        }
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
        Size = new Size((int)(380 * scale), (int)(60 * scale));
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

    // Only animate while it is on and the window is actually showing.
    void UpdateAnim() { anim.Enabled = Visible && isChecked; }
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
        g.Clear(Parent != null ? Parent.BackColor : Theme.Back);

        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        int radius = Math.Max(10, Height / 4);
        using (var path = RoundRect(r, radius))
        {
            // On: a slowly moving rainbow (RGB). Off: plain grey, so the difference is easy to see.
            string text = isChecked ? "\u2713  ON  (click to turn off)" : "OFF  -  click to TURN ON";
            if (isChecked)
            {
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
            }
            else
            {
                Color top = Theme.Dark ? Color.FromArgb(70, 70, 78) : Color.FromArgb(216, 218, 224);
                Color bottom = Theme.Dark ? Color.FromArgb(54, 54, 61) : Color.FromArgb(196, 198, 206);
                using (var br = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical)) g.FillPath(br, path);
            }
            if (hover || down)
                using (var tint = new SolidBrush(down ? Color.FromArgb(50, 0, 0, 0) : Color.FromArgb(35, 255, 255, 255)))
                    g.FillPath(tint, path);

            using (var pen = new Pen(Color.FromArgb(Theme.Dark ? 120 : 60, 0, 0, 0), 1.5f)) g.DrawPath(pen, path);

            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            Color textCol = isChecked ? Color.White : (Theme.Dark ? Color.FromArgb(200, 200, 210) : Color.FromArgb(80, 80, 92));
            if (isChecked)
            {
                var shadowRect = new RectangleF(r.X + 1, r.Y + 2, r.Width, r.Height);
                using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.DrawString(text, Font, shadow, shadowRect, fmt);
            }
            using (var tb = new SolidBrush(textCol)) g.DrawString(text, Font, tb, r, fmt);
            if (Focused)
                using (var pen = new Pen(textCol, 1f) { DashStyle = DashStyle.Dot })
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
    public bool RandomInHours = true;  // the range is shown in hours (true) or minutes (false)
    // Warning and beep are off by default: usually the person is asleep.
    public bool Warn = false;
    public bool Beep = false;
    public bool CountMouse = true;
    public bool CountKeyboard = true;
    public bool CountController = true;
    // OLED mode (off at first) and its options, set up in the OLED mode window.
    public bool Oled = false;
    public bool Saver, ScreenOff, OffOnLock, HideTaskbar, HidePointer, CoverUnused, BreakReminder;
    public int SaverMinutes, ScreenOffMinutes, HideTaskbarMinutes, HidePointerSeconds, CoverUnusedMinutes, BreakHours;
    // Idle Sleep switched the taskbar to auto-hide (while nobody uses the PC) and must switch it back.
    public bool TaskbarChanged = false;
    // -1 = follow Windows' light/dark setting, 0 = light, 1 = dark.
    public int Dark = -1;
    // Close World of Warcraft when nobody uses it (only while Idle Sleep is on).
    public bool WowClose = false;
    public bool WowAfk = true;          // after a while with no mouse/keyboard/controller use
    public int WowAfkMinutes = 20;
    public bool WowLogin = true;        // after a while sitting at the login screen (disconnected)
    public int WowLoginMinutes = 10;
    // Night light: switch it on and off by itself at the times learned from you, and how warm.
    public bool NightAuto = true;
    public int NightStrength = 80;

    public Settings() { OledRecommended(); }

    // The OLED options that help most without getting in the way.
    public void OledRecommended()
    {
        Saver = true; SaverMinutes = 5;
        ScreenOff = true; ScreenOffMinutes = 10;
        OffOnLock = true;
        HideTaskbar = false; HideTaskbarMinutes = 2;
        HidePointer = true; HidePointerSeconds = 5;
        CoverUnused = false; CoverUnusedMinutes = 10;
        BreakReminder = false; BreakHours = 3;
    }

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
            bool sawOled = false, oldScreenCare = false, sawTaskbarMinutes = false;
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
                else if (key == "RandomMinMinutes" && int.TryParse(val, out n)) s.RandomMinMinutes = Math.Max(1, Math.Min(1440, n));
                else if (key == "RandomMaxMinutes" && int.TryParse(val, out n)) s.RandomMaxMinutes = Math.Max(1, Math.Min(1440, n));
                else if (key == "RandomUnit") s.RandomInHours = val != "minutes";
                else if (key == "Warn") s.Warn = val == "1";
                else if (key == "Beep") s.Beep = val == "1";
                else if (key == "CountMouse") s.CountMouse = val == "1";
                else if (key == "CountKeyboard") s.CountKeyboard = val == "1";
                else if (key == "CountController") s.CountController = val == "1";
                else if (key == "Oled") { s.Oled = val == "1"; sawOled = true; }
                else if (key == "Saver") { s.Saver = val == "1"; oldScreenCare |= s.Saver; }
                else if (key == "SaverMinutes" && int.TryParse(val, out n)) s.SaverMinutes = Math.Max(1, Math.Min(720, n));
                else if (key == "ScreenOff") { s.ScreenOff = val == "1"; oldScreenCare |= s.ScreenOff; }
                else if (key == "ScreenOffMinutes" && int.TryParse(val, out n)) s.ScreenOffMinutes = Math.Max(1, Math.Min(720, n));
                else if (key == "OffOnLock") s.OffOnLock = val == "1";
                else if (key == "HideTaskbar") s.HideTaskbar = val == "1";
                else if (key == "HideTaskbarMinutes" && int.TryParse(val, out n)) { s.HideTaskbarMinutes = Math.Max(1, Math.Min(720, n)); sawTaskbarMinutes = true; }
                else if (key == "HidePointer") s.HidePointer = val == "1";
                else if (key == "HidePointerSeconds" && int.TryParse(val, out n)) s.HidePointerSeconds = Math.Max(1, Math.Min(600, n));
                else if (key == "CoverUnused") s.CoverUnused = val == "1";
                else if (key == "CoverUnusedMinutes" && int.TryParse(val, out n)) s.CoverUnusedMinutes = Math.Max(1, Math.Min(720, n));
                else if (key == "BreakReminder") s.BreakReminder = val == "1";
                else if (key == "BreakHours" && int.TryParse(val, out n)) s.BreakHours = Math.Max(1, Math.Min(12, n));
                else if (key == "TaskbarChanged") s.TaskbarChanged = val == "1";
                else if (key == "Dark" && int.TryParse(val, out n)) s.Dark = Math.Max(-1, Math.Min(1, n));
                else if (key == "WowClose") s.WowClose = val == "1";
                else if (key == "WowAfk") s.WowAfk = val == "1";
                else if (key == "WowAfkMinutes" && int.TryParse(val, out n)) s.WowAfkMinutes = Math.Max(2, Math.Min(600, n));
                else if (key == "WowLogin") s.WowLogin = val == "1";
                else if (key == "WowLoginMinutes" && int.TryParse(val, out n)) s.WowLoginMinutes = Math.Max(2, Math.Min(600, n));
                else if (key == "NightAuto") s.NightAuto = val == "1";
                else if (key == "NightStrength" && int.TryParse(val, out n)) s.NightStrength = Math.Max(5, Math.Min(100, n));
            }
            if (s.RandomMaxMinutes < s.RandomMinMinutes) s.RandomMaxMinutes = s.RandomMinMinutes;
            // The first 2.1 hid the taskbar all the time; now it only hides while nobody uses the PC, off at first.
            if (!sawTaskbarMinutes) s.HideTaskbar = false;
            // Version 2.0 had no OLED mode switch, only the screen care boxes (both off unless used).
            if (!sawOled)
            {
                s.Oled = oldScreenCare;
                if (!oldScreenCare) { s.Saver = true; s.ScreenOff = true; }
            }
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
                "RandomUnit=" + (RandomInHours ? "hours" : "minutes"),
                "Warn=" + (Warn ? "1" : "0"),
                "Beep=" + (Beep ? "1" : "0"),
                "CountMouse=" + (CountMouse ? "1" : "0"),
                "CountKeyboard=" + (CountKeyboard ? "1" : "0"),
                "CountController=" + (CountController ? "1" : "0"),
                "Oled=" + (Oled ? "1" : "0"),
                "Saver=" + (Saver ? "1" : "0"),
                "SaverMinutes=" + SaverMinutes,
                "ScreenOff=" + (ScreenOff ? "1" : "0"),
                "ScreenOffMinutes=" + ScreenOffMinutes,
                "OffOnLock=" + (OffOnLock ? "1" : "0"),
                "HideTaskbar=" + (HideTaskbar ? "1" : "0"),
                "HideTaskbarMinutes=" + HideTaskbarMinutes,
                "HidePointer=" + (HidePointer ? "1" : "0"),
                "HidePointerSeconds=" + HidePointerSeconds,
                "CoverUnused=" + (CoverUnused ? "1" : "0"),
                "CoverUnusedMinutes=" + CoverUnusedMinutes,
                "BreakReminder=" + (BreakReminder ? "1" : "0"),
                "BreakHours=" + BreakHours,
                "TaskbarChanged=" + (TaskbarChanged ? "1" : "0"),
                "Dark=" + Dark,
                "WowClose=" + (WowClose ? "1" : "0"),
                "WowAfk=" + (WowAfk ? "1" : "0"),
                "WowAfkMinutes=" + WowAfkMinutes,
                "WowLogin=" + (WowLogin ? "1" : "0"),
                "WowLoginMinutes=" + WowLoginMinutes,
                "NightAuto=" + (NightAuto ? "1" : "0"),
                "NightStrength=" + NightStrength,
            });
        }
        catch { }
    }
}

class WarningForm : Form
{
    Label lblTime, lblHint;
    public event Action StayAwake;
    public string Lead = "This PC will";  // who is about to do it
    public string Phrase = "fall asleep"; // what the PC is about to do
    public string Hint { set { lblHint.Text = value; } }

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
        lblHint = new Label { AutoSize = true, Tag = "sub", Text = "Move the mouse to stop it, or click the button.", Margin = new Padding(3, 4, 3, 8) };
        var btn = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        btn.Click += delegate { FireStayAwake(); };
        panel.Controls.Add(lblTime);
        panel.Controls.Add(lblHint);
        panel.Controls.Add(btn);
        Controls.Add(panel);
        SetSeconds(60);
        Retheme();
    }

    public void Retheme()
    {
        Theme.Apply(this, Theme.Back);
        Theme.TitleBar(this);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.TitleBar(this); }

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
            ? Lead + " " + Phrase + " in 1 minute"
            : string.Format("{0} {1} in {2} second{3}", Lead, Phrase, secs, secs == 1 ? "" : "s");
    }

    // 'raise' lifts it up, so two warnings can show at once without covering each other.
    public void PlaceBottomRight(int raise = 0)
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 20, wa.Bottom - Height - 20 - raise);
    }
}

// Full-screen black cover for one monitor. On an OLED screen black pixels are switched off, so nothing
// wears in while nobody is looking. The mouse pointer is hidden and nothing else is shown.
class SaverForm : Form
{
    static Cursor blank;

    public SaverForm(Screen screen)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        BackColor = Color.Black;
        if (blank == null)
        {
            try { blank = new Cursor(Native.BlankCursor()); }
            catch { blank = Cursors.Default; }
        }
        Cursor = blank;
    }

    // On top of everything, but never takes focus away from a game.
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x8 /* TOPMOST */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */;
            return cp;
        }
    }
}

// The OLED mode window: every way Idle Sleep can protect an OLED screen, with the recommended ones ticked.
// Changes are used straight away; Changed tells the main window to save them.
class OledForm : Form
{
    readonly Settings s;
    readonly float scale;
    public event Action Changed;
    bool loading;
    CheckBox chkOn, chkSaver, chkOff, chkLock, chkTaskbar, chkPointer, chkCover, chkBreak;
    NumericUpDown numSaver, numOff, numTaskbar, numPointer, numCover, numBreak;
    readonly List<Control> optionText = new List<Control>(); // shown softer while OLED mode is off

    public OledForm(Settings settings, Icon icon, float dpiScale)
    {
        s = settings;
        scale = dpiScale;
        Text = "OLED mode";
        Icon = icon;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(S(16), S(12), S(16), S(12)),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "OLED mode", AutoSize = true, Font = new Font("Segoe UI", 17f, FontStyle.Bold), Margin = new Padding(0) });
        root.Controls.Add(new Label
        {
            AutoSize = true, Tag = "sub", Margin = new Padding(S(2), 0, 0, S(10)),
            Text = "An OLED screen can wear in (burn-in) where the picture stays the same\nfor a long time. Pick what helps. The ticks you start with are the recommended ones.",
        });
        chkOn = new CheckBox { Text = "Turn on OLED protection", AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(10)) };
        chkOn.CheckedChanged += delegate { Save(); };
        root.Controls.Add(chkOn);

        var cardIdle = new Card();
        cardIdle.Controls.Add(Heading("WHEN NOBODY USES THE PC"));
        var g1 = Grid();
        chkSaver = AddRow(g1, "Black screen saver after", numSaver = Num(1, 720), "minutes");
        chkOff = AddRow(g1, "Turn the screen off after", numOff = Num(1, 720), "minutes");
        chkLock = AddRow(g1, "Turn the screen off when the PC is locked (Windows key + L)", null, null);
        chkTaskbar = AddRow(g1, "Hide the taskbar after", numTaskbar = Num(1, 720), "minutes");
        cardIdle.Controls.Add(g1);
        root.Controls.Add(cardIdle);

        var cardUse = new Card();
        cardUse.Controls.Add(Heading("WHILE YOU USE THE PC"));
        var g2 = Grid();
        chkPointer = AddRow(g2, "Hide the mouse pointer when it is still for", numPointer = Num(1, 600), "seconds");
        chkCover = AddRow(g2, "Black out a screen the mouse has not been on for", numCover = Num(1, 720), "minutes");
        AddHint(g2, "For 2 or more screens. Moving the mouse onto it brings it back.");
        chkBreak = AddRow(g2, "Remind me to take a break after", numBreak = Num(1, 12), "hours of use");
        AddHint(g2, "Game bars and maps sit in one place, so breaks help the screen too.");
        cardUse.Controls.Add(g2);
        root.Controls.Add(cardUse);

        root.Controls.Add(new Label
        {
            AutoSize = true, Tag = "sub", Margin = new Padding(S(2), 0, 0, S(10)),
            Text = "Any use brings everything back the way it was. Works even when the big button is off.",
        });

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var btnReset = new Button { Text = "Back to recommended", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        btnReset.Click += delegate { s.OledRecommended(); s.Oled = true; LoadValues(); Save(); };
        var btnClose = new Button { Text = "Close", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        btnClose.Click += delegate { Close(); };
        buttons.Controls.Add(btnReset);
        buttons.Controls.Add(btnClose);
        root.Controls.Add(buttons);
        Controls.Add(root);

        LoadValues();
        Retheme();
    }

    int S(int px) { return (int)Math.Round(px * scale); }

    Label Heading(string text)
    {
        return new Label { Text = text, AutoSize = true, Tag = "head", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(4)) };
    }

    static TableLayoutPanel Grid()
    {
        return new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
    }

    NumericUpDown Num(int min, int max)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Width = S(62), Anchor = AnchorStyles.Left };
        n.ValueChanged += delegate { Save(); };
        return n;
    }

    // [x] text [ n ] unit, one line of a grid so the number boxes line up. Without a number the text uses the whole line.
    CheckBox AddRow(TableLayoutPanel g, string text, NumericUpDown num, string unit)
    {
        var chk = new CheckBox { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(2), S(4), S(2)) };
        chk.CheckedChanged += delegate { Save(); };
        g.Controls.Add(chk);
        optionText.Add(chk);
        if (num == null) { g.SetColumnSpan(chk, 3); return chk; }
        g.Controls.Add(num);
        var lbl = new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Left };
        g.Controls.Add(lbl);
        optionText.Add(lbl);
        return chk;
    }

    void AddHint(TableLayoutPanel g, string text)
    {
        var lbl = new Label { Text = text, AutoSize = true, Tag = "sub", Margin = new Padding(S(18), 0, 0, S(4)) };
        g.Controls.Add(lbl);
        g.SetColumnSpan(lbl, 3);
    }

    void LoadValues()
    {
        loading = true;
        chkOn.Checked = s.Oled;
        chkSaver.Checked = s.Saver; numSaver.Value = s.SaverMinutes;
        chkOff.Checked = s.ScreenOff; numOff.Value = s.ScreenOffMinutes;
        chkLock.Checked = s.OffOnLock;
        chkTaskbar.Checked = s.HideTaskbar; numTaskbar.Value = s.HideTaskbarMinutes;
        chkPointer.Checked = s.HidePointer; numPointer.Value = s.HidePointerSeconds;
        chkCover.Checked = s.CoverUnused; numCover.Value = s.CoverUnusedMinutes;
        chkBreak.Checked = s.BreakReminder; numBreak.Value = s.BreakHours;
        loading = false;
        UpdateLook();
    }

    void Save()
    {
        if (loading) return;
        s.Oled = chkOn.Checked;
        s.Saver = chkSaver.Checked; s.SaverMinutes = (int)numSaver.Value;
        s.ScreenOff = chkOff.Checked; s.ScreenOffMinutes = (int)numOff.Value;
        s.OffOnLock = chkLock.Checked;
        s.HideTaskbar = chkTaskbar.Checked; s.HideTaskbarMinutes = (int)numTaskbar.Value;
        s.HidePointer = chkPointer.Checked; s.HidePointerSeconds = (int)numPointer.Value;
        s.CoverUnused = chkCover.Checked; s.CoverUnusedMinutes = (int)numCover.Value;
        s.BreakReminder = chkBreak.Checked; s.BreakHours = (int)numBreak.Value;
        UpdateLook();
        if (Changed != null) Changed();
    }

    // While OLED mode is off the options are shown in a softer colour (still easy to read and change).
    void UpdateLook()
    {
        foreach (var c in optionText) c.ForeColor = chkOn.Checked ? Theme.Text : Theme.Sub;
    }

    public void Retheme()
    {
        Theme.Apply(this, Theme.Back);
        Theme.TitleBar(this);
        UpdateLook();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.TitleBar(this); }

    // Opens over the middle of the main window, kept inside the screen.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Owner != null ? Screen.FromControl(Owner).WorkingArea : Screen.PrimaryScreen.WorkingArea;
        int x = Owner != null ? Owner.Left + (Owner.Width - Width) / 2 : area.Left + (area.Width - Width) / 2;
        int y = Owner != null ? Owner.Top + (Owner.Height - Height) / 3 : area.Top + (area.Height - Height) / 3;
        Location = new Point(Math.Max(area.Left, Math.Min(x, area.Right - Width)), Math.Max(area.Top, Math.Min(y, area.Bottom - Height)));
    }
}

class MainForm : Form
{
    public const string Version = "2.2"; // keep in step with AssemblyVersion at the top
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "IdleSleep";

    Settings settings = Settings.Load();
    NotifyIcon tray;
    System.Windows.Forms.Timer timer;
    WarningForm warning;
    BigToggle chkOn;
    CheckBox chkWarn, chkBeep, chkMouse, chkKeys, chkPad, chkStartup, chkForce;
    CheckBox chkWow, chkWowAfk, chkWowLogin;
    NumericUpDown numWowAfk, numWowLogin;
    FlowLayoutPanel wowRows;
    ToolStripMenuItem menuWow;
    RadioButton[] radAction;
    Button btnNow, btnTheme;
    float dpiScale = 1f;
    NumericUpDown numMin;
    CheckBox chkRandom;
    FlowLayoutPanel timeRow, randRow;
    NumericUpDown numRandMin, numRandMax; // random range, in minutes or hours
    ComboBox cmbRandUnit;
    Random rng = new Random();
    uint randomLimitMs, lastIdleMs; // the hidden random wait, and last tick's idle time
    Label lblStatus;
    ToolStripMenuItem menuToggle, menuNow;

    static readonly string[] ActionNames = { "Sleep", "Hibernate", "Shut down", "Restart", "Lock" };
    // Fits "This PC will ___ in 1 minute".
    static readonly string[] ActionPhrases = { "fall asleep", "hibernate", "shut down", "restart", "lock" };
    Icon iconOn, iconOff;

    bool startHidden, reallyExit, sleeping, shownHideTip, loading, beeped;
    uint armedTick, lastPadTick, suspendStartTick;
    uint lastPadRealTick; // last real controller use (0 = none yet); lastPadTick is also reset on wake
    volatile bool resumeFlag, failedSuspend; // set from other threads, handled by the timer
    Native.XINPUT_GAMEPAD[] padPrev = new Native.XINPUT_GAMEPAD[4];
    bool[] padSeen = new bool[4];
    uint[] padPacket = new uint[4];
    bool padApiOk = true;
    string lastCause = ""; // what last counted as using the PC, shown in the status line
    uint unseenLogTick, frontPid = uint.MaxValue;
    string frontName = "";
    bool frontHidden;
    static readonly bool selfElevated = Native.IsElevated(Native.GetCurrentProcess());
    // OLED mode.
    Button btnOled;
    Label lblOled;
    OledForm oledForm;
    SaverForm[] savers;
    // Fast checks (10 a second): close the saver on any use, hide/show the pointer, uncover a screen.
    System.Windows.Forms.Timer fastTimer;
    uint screenArmTick, screenOffTick, saverMouse, saverKey, saverShownTick, useStartTick, lockTick;
    bool displayOn = true; // Windows tells us when the screen goes off or on
    volatile bool locked, unlockFlag; // set from a Windows helper thread
    bool lockOffDone, windowsEnding;
    // Screens blacked out because the mouse has not been there, and when the mouse was last on each screen.
    readonly Dictionary<string, SaverForm> covers = new Dictionary<string, SaverForm>();
    readonly Dictionary<string, uint> screenSeen = new Dictionary<string, uint>();

    public MainForm(bool startHidden)
    {
        this.startHidden = startHidden;
        // Opened by hand: always start turned off. Started by Windows (/tray): start turned on.
        settings.Enabled = startHidden;
        Theme.Dark = settings.Dark == -1 ? Theme.WindowsIsDark() : settings.Dark == 1;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;
        iconOn = MakeMoonIcon(true);
        iconOff = MakeMoonIcon(false);

        Text = "Idle Sleep";
        // The program's own icon (moon on a night sky) for the window and taskbar.
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { Icon = iconOn; }
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
        cmbRandUnit.SelectedIndex = settings.RandomInHours ? 1 : 0;
        SetRandUnit(settings.RandomInHours);
        chkRandom.Checked = settings.RandomTime;
        chkWarn.Checked = settings.Warn;
        chkBeep.Checked = settings.Beep;
        chkMouse.Checked = settings.CountMouse;
        chkKeys.Checked = settings.CountKeyboard;
        chkPad.Checked = settings.CountController;
        chkWow.Checked = settings.WowClose;
        chkWowAfk.Checked = settings.WowAfk;
        numWowAfk.Value = settings.WowAfkMinutes;
        chkWowLogin.Checked = settings.WowLogin;
        numWowLogin.Value = settings.WowLoginMinutes;
        menuWow.Checked = settings.WowClose;
        chkNightAuto.Checked = settings.NightAuto;
        menuNightAuto.Checked = settings.NightAuto;
        trkNight.Value = settings.NightStrength;
        NightFilter.Strength = settings.NightStrength;
        MoveOldStartup();
        chkStartup.Checked = GetStartup();
        loading = false;
        UpdateActionText();
        Retheme();

        ResetIdle();
        InputWatcher.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // A pointer left hidden by a run that was stopped suddenly comes back here; the taskbar too.
        if (settings.HidePointer) PointerHider.Restore();
        AppDomain.CurrentDomain.ProcessExit += delegate { PointerHider.Show(); NightFilter.Restore(); };
        ApplyTaskbar(false);

        // The night light was on when Idle Sleep last stopped: bring it back (quickly).
        if (night.On) { nightTarget = 1; nightStep = 1 / 15.0; }

        fastTimer = new System.Windows.Forms.Timer { Interval = 100 };
        fastTimer.Tick += delegate { FastTick(); };
        fastTimer.Start();

        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += delegate { OnTick(); };
        timer.Start();
        OnTick();
    }

    void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(S(16), S(12), S(16), S(12)),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Title on the left, light/dark switch on the right.
        var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, S(10)) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        titles.Controls.Add(new Label { Text = "Idle Sleep", AutoSize = true, Font = new Font("Segoe UI", 17f, FontStyle.Bold), Margin = new Padding(0) });
        titles.Controls.Add(new Label
        {
            AutoSize = true, Tag = "sub", Margin = new Padding(S(2), 0, 0, 0),
            Text = "Puts the PC to sleep when nobody uses it.\nGames and programs can stay open.",
        });
        header.Controls.Add(titles, 0, 0);
        btnTheme = MakeButton("", delegate { ToggleTheme(); });
        btnTheme.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnTheme.Margin = new Padding(S(10), S(6), 0, 0);
        header.Controls.Add(btnTheme, 1, 0);
        root.Controls.Add(header);

        chkOn = new BigToggle { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, S(12)) };
        chkOn.CheckedChanged += delegate { if (!loading && chkOn.Checked) ResetIdle(); Apply(); };
        root.Controls.Add(chkOn);

        lblStatus = new Label { AutoSize = true, Margin = new Padding(S(2), 0, 0, S(10)), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        root.Controls.Add(lblStatus);

        // Two columns, so the window fits on a 1080p screen: the sleep settings on the left,
        // World of Warcraft and the night light on the right.
        var cols = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0) };
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = Column();
        left.Margin = new Padding(0, 0, S(12), 0);
        var right = Column();
        cols.Controls.Add(left, 0, 0);
        cols.Controls.Add(right, 1, 0);
        root.Controls.Add(cols);

        // What to do. Radio buttons in one panel = only one can be chosen.
        var cardDo = new Card();
        cardDo.Controls.Add(Heading("WHEN NOBODY USES THE PC, DO THIS"));
        var actionRow = Row();
        radAction = new RadioButton[ActionNames.Length];
        for (int i = 0; i < ActionNames.Length; i++)
        {
            var rb = new RadioButton { Text = ActionNames[i], AutoSize = true, Margin = new Padding(0, S(2), S(12), S(2)) };
            rb.CheckedChanged += delegate(object s, EventArgs ev) { if (((RadioButton)s).Checked) { UpdateActionText(); Apply(); } };
            radAction[i] = rb;
            actionRow.Controls.Add(rb);
        }
        cardDo.Controls.Add(actionRow);
        chkForce = new CheckBox { Text = "Force programs to close (unsaved work is lost)", AutoSize = true, Margin = new Padding(0, S(4), 0, 0) };
        chkForce.CheckedChanged += delegate { Apply(); };
        cardDo.Controls.Add(chkForce);
        left.Controls.Add(cardDo);

        // How long.
        var cardTime = new Card();
        cardTime.Controls.Add(Heading("HOW LONG"));
        timeRow = Row();
        timeRow.Controls.Add(new Label { Text = "After", AutoSize = true, Anchor = AnchorStyles.Left });
        numMin = new NumericUpDown { Minimum = 1, Maximum = 720, Width = S(70), Anchor = AnchorStyles.Left };
        numMin.ValueChanged += delegate { Apply(); };
        timeRow.Controls.Add(numMin);
        timeRow.Controls.Add(new Label { Text = "minutes of no use", AutoSize = true, Anchor = AnchorStyles.Left });
        cardTime.Controls.Add(timeRow);

        chkRandom = new CheckBox { Text = "Use a random time instead (not shown)", AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
        chkRandom.CheckedChanged += delegate { UpdateTimeRows(); Apply(); };
        cardTime.Controls.Add(chkRandom);

        randRow = Row();
        randRow.Margin = new Padding(S(18), S(2), 0, 0);
        randRow.Controls.Add(new Label { Text = "Between", AutoSize = true, Anchor = AnchorStyles.Left });
        numRandMin = new NumericUpDown { Width = S(62), Anchor = AnchorStyles.Left };
        numRandMax = new NumericUpDown { Width = S(62), Anchor = AnchorStyles.Left };
        // Keep the shortest from going above the longest.
        numRandMin.ValueChanged += delegate { if (!loading && numRandMin.Value > numRandMax.Value) numRandMax.Value = numRandMin.Value; Apply(); };
        numRandMax.ValueChanged += delegate { if (!loading && numRandMax.Value < numRandMin.Value) numRandMin.Value = numRandMax.Value; Apply(); };
        randRow.Controls.Add(numRandMin);
        randRow.Controls.Add(new Label { Text = "and", AutoSize = true, Anchor = AnchorStyles.Left });
        randRow.Controls.Add(numRandMax);
        cmbRandUnit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(88), Anchor = AnchorStyles.Left };
        cmbRandUnit.Items.AddRange(new object[] { "minutes", "hours" });
        cmbRandUnit.SelectedIndexChanged += delegate
        {
            if (loading) return;
            settings.RandomInHours = cmbRandUnit.SelectedIndex == 1;
            SetRandUnit(settings.RandomInHours);
            Apply();
        };
        randRow.Controls.Add(cmbRandUnit);
        cardTime.Controls.Add(randRow);
        left.Controls.Add(cardTime);

        // What counts as use, all on one line.
        var cardUse = new Card();
        cardUse.Controls.Add(Heading("WHAT COUNTS AS USING THE PC"));
        var useRow = Row();
        var m = new Padding(0, S(2), S(14), S(2));
        chkMouse = new CheckBox { Text = "Mouse", AutoSize = true, Margin = m };
        chkKeys = new CheckBox { Text = "Keyboard", AutoSize = true, Margin = m };
        chkPad = new CheckBox { Text = "Controller (Xbox-style)", AutoSize = true, Margin = m };
        foreach (var c in new[] { chkMouse, chkKeys, chkPad })
        {
            c.CheckedChanged += delegate { Apply(); };
            useRow.Controls.Add(c);
        }
        cardUse.Controls.Add(useRow);
        left.Controls.Add(cardUse);

        // OLED mode: the button opens its own window with all the options.
        var cardOled = new Card();
        cardOled.Controls.Add(Heading("OLED SCREEN CARE"));
        var oledRow = Row();
        btnOled = MakeButton("OLED mode", delegate { OpenOled(); });
        btnOled.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        btnOled.Margin = new Padding(0, 0, S(10), 0);
        oledRow.Controls.Add(btnOled);
        lblOled = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        oledRow.Controls.Add(lblOled);
        cardOled.Controls.Add(oledRow);
        left.Controls.Add(cardOled);

        // Closing World of Warcraft: one on/off switch, with the two rules under it.
        var cardWow = new Card();
        cardWow.Controls.Add(Heading("WORLD OF WARCRAFT"));
        chkWow = new CheckBox { Text = "Close WoW when nobody is using it", AutoSize = true, Margin = new Padding(0, S(2), 0, S(2)) };
        chkWow.CheckedChanged += delegate { UpdateWowRows(); Apply(); };
        cardWow.Controls.Add(chkWow);
        wowRows = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(S(18), 0, 0, 0),
        };
        chkWowAfk = new CheckBox { Text = "After", AutoSize = true, Anchor = AnchorStyles.Left };
        numWowAfk = new NumericUpDown { Minimum = 2, Maximum = 600, Width = S(62), Anchor = AnchorStyles.Left };
        var afkRow = Row();
        afkRow.Controls.Add(chkWowAfk);
        afkRow.Controls.Add(numWowAfk);
        afkRow.Controls.Add(new Label { Text = "minutes of no use (in game too)", AutoSize = true, Anchor = AnchorStyles.Left });
        wowRows.Controls.Add(afkRow);
        chkWowLogin = new CheckBox { Text = "After", AutoSize = true, Anchor = AnchorStyles.Left };
        numWowLogin = new NumericUpDown { Minimum = 2, Maximum = 600, Width = S(62), Anchor = AnchorStyles.Left };
        var loginRow = Row();
        loginRow.Controls.Add(chkWowLogin);
        loginRow.Controls.Add(numWowLogin);
        loginRow.Controls.Add(new Label { Text = "minutes at the login screen", AutoSize = true, Anchor = AnchorStyles.Left });
        wowRows.Controls.Add(loginRow);
        wowRows.Controls.Add(new Label
        {
            AutoSize = true, Tag = "sub", Margin = new Padding(0, S(4), 0, 0),
            Text = "Login screen = never logged in, or disconnected.\nShows a 1-minute warning first, with Cancel. Only while Idle Sleep is on.",
        });
        foreach (var c in new[] { chkWowAfk, chkWowLogin }) c.CheckedChanged += delegate { Apply(); };
        numWowAfk.ValueChanged += delegate { Apply(); };
        numWowLogin.ValueChanged += delegate { Apply(); };
        cardWow.Controls.Add(wowRows);
        right.Controls.Add(cardWow);

        // Night light: Idle Sleep's own warmer colours, by hand or by itself at the times learned from you.
        var cardNight = new Card();
        cardNight.Controls.Add(Heading("NIGHT LIGHT (WARMER COLOURS)"));
        var nightRow = Row();
        btnNight = MakeButton("Turn night light on", delegate { NightSwitch(!night.On, true, "you, with the button"); });
        btnNight.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        btnNight.Margin = new Padding(0, 0, S(12), 0);
        nightRow.Controls.Add(btnNight);
        chkNightAuto = new CheckBox { Text = "Turn on and off by itself", AutoSize = true, Anchor = AnchorStyles.Left };
        chkNightAuto.CheckedChanged += delegate { Apply(); };
        nightRow.Controls.Add(chkNightAuto);
        cardNight.Controls.Add(nightRow);
        var strengthRow = Row();
        strengthRow.Margin = new Padding(0, S(6), 0, 0);
        strengthRow.Controls.Add(new Label { Text = "Strength", AutoSize = true, Anchor = AnchorStyles.Left });
        trkNight = new TrackBar
        {
            Minimum = 5, Maximum = 100, SmallChange = 5, LargeChange = 10, TickStyle = TickStyle.None,
            AutoSize = false, Width = S(180), Height = S(28), Anchor = AnchorStyles.Left,
        };
        trkNight.ValueChanged += delegate { NightStrengthChanged(); };
        strengthRow.Controls.Add(trkNight);
        lblNightStrength = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        strengthRow.Controls.Add(lblNightStrength);
        cardNight.Controls.Add(strengthRow);
        lblNight = new Label { AutoSize = true, Margin = new Padding(0, S(4), 0, 0) };
        cardNight.Controls.Add(lblNight);
        lblNightLearned = new Label { AutoSize = true, Tag = "sub", Margin = new Padding(0, S(2), 0, 0) };
        cardNight.Controls.Add(lblNightLearned);
        cardNight.Controls.Add(new Label
        {
            AutoSize = true, Tag = "sub", Margin = new Padding(0, S(6), 0, 0),
            Text = "Your day starts when you come back after 5+ hours away. It uses the average\nof your last 7 times. Leave Windows' own Night light off, or the two add up.",
        });
        right.Controls.Add(cardNight);

        // Extras.
        var cardMore = new Card();
        cardMore.Controls.Add(Heading("EXTRAS"));
        chkWarn = new CheckBox { Text = "Show a little warning 1 minute before (with Cancel)", AutoSize = true, Margin = new Padding(0, S(2), 0, S(2)) };
        chkWarn.CheckedChanged += delegate { Apply(); };
        cardMore.Controls.Add(chkWarn);
        chkBeep = new CheckBox { Text = "Beep 1 minute before", AutoSize = true, Margin = new Padding(0, S(2), 0, S(2)) };
        chkBeep.CheckedChanged += delegate { Apply(); };
        cardMore.Controls.Add(chkBeep);
        chkStartup = new CheckBox { Text = "Start automatically when Windows starts", AutoSize = true, Margin = new Padding(0, S(2), 0, S(2)) };
        chkStartup.CheckedChanged += delegate { if (!loading) SetStartup(chkStartup.Checked); };
        cardMore.Controls.Add(chkStartup);
        left.Controls.Add(cardMore);

        var buttons = Row();
        buttons.Margin = new Padding(0, S(2), 0, 0);
        btnNow = MakeButton("Sleep now", delegate { NowClicked("Now button"); });
        buttons.Controls.Add(btnNow);
        buttons.Controls.Add(MakeButton("Hide", delegate { HideToTray(); }));
        buttons.Controls.Add(MakeButton("Exit", delegate { ExitApp(); }));
        right.Controls.Add(buttons);

        right.Controls.Add(new Label
        {
            AutoSize = true, Tag = "sub", Margin = new Padding(S(2), S(10), 0, 0),
            Text = "Closing this window keeps Idle Sleep running (moon icon by the clock).\nUse Exit to stop it completely.\nVersion " + Version,
        });

        Controls.Add(root);
    }

    // One column of cards.
    static TableLayoutPanel Column()
    {
        var c = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0),
        };
        c.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return c;
    }

    Label Heading(string text)
    {
        return new Label { Text = text, AutoSize = true, Tag = "head", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(4)) };
    }

    // Sets up the two random-range boxes for minutes or hours, showing the saved range in that unit.
    void SetRandUnit(bool hours)
    {
        bool was = loading;
        loading = true;
        foreach (var n in new[] { numRandMin, numRandMax })
        {
            if (hours) { n.DecimalPlaces = 1; n.Increment = 0.5m; n.Minimum = 0.1m; n.Maximum = 24m; }
            else { n.DecimalPlaces = 0; n.Increment = 5m; n.Minimum = 1m; n.Maximum = 1440m; }
        }
        decimal per = hours ? 60m : 1m;
        numRandMin.Value = Fit(numRandMin, Math.Round(settings.RandomMinMinutes / per, 1));
        numRandMax.Value = Fit(numRandMax, Math.Round(settings.RandomMaxMinutes / per, 1));
        loading = was;
    }

    static decimal Fit(NumericUpDown n, decimal v) { return Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }

    // The row not in use is shown in a softer colour (not greyed out, so it stays easy to read and change).
    void UpdateTimeRows()
    {
        bool random = chkRandom.Checked;
        foreach (Control c in timeRow.Controls) c.ForeColor = random ? Theme.Sub : Theme.Text;
        foreach (Control c in randRow.Controls) c.ForeColor = random ? Theme.Text : Theme.Sub;
    }

    void OpenOled()
    {
        if (oledForm != null && !oledForm.IsDisposed) { oledForm.Activate(); return; }
        oledForm = new OledForm(settings, Icon, dpiScale);
        oledForm.Changed += delegate { OledChanged(); };
        oledForm.Show(this);
    }

    void OledChanged()
    {
        settings.Save();
        UpdateOledText();
        OnTick();
    }

    // "On: saver after 5 min, screen off after 10 min, +3 more" next to the OLED mode button.
    void UpdateOledText()
    {
        if (!settings.Oled)
        {
            lblOled.Text = "Off. Helps stop burn-in on OLED screens.";
            lblOled.ForeColor = Theme.Sub;
            return;
        }
        var on = new List<string>();
        if (settings.Saver) on.Add("saver after " + settings.SaverMinutes + " min");
        if (settings.ScreenOff) on.Add("screen off after " + settings.ScreenOffMinutes + " min");
        if (settings.OffOnLock) on.Add("off when locked");
        if (settings.HideTaskbar) on.Add("taskbar hides after " + settings.HideTaskbarMinutes + " min");
        if (settings.HidePointer) on.Add("pointer hides");
        if (settings.CoverUnused) on.Add("unused screen black");
        if (settings.BreakReminder) on.Add("break reminder");
        string text = on.Count == 0 ? "nothing ticked" : string.Join(", ", on.GetRange(0, Math.Min(2, on.Count)).ToArray());
        if (on.Count > 2) text += ", +" + (on.Count - 2) + " more";
        lblOled.Text = "On: " + text;
        lblOled.ForeColor = Theme.Text;
    }

    // While nobody uses the PC, Windows' "automatically hide the taskbar" is switched on, and switched back
    // off as soon as the PC is used again (only if Idle Sleep was the one that switched it on).

    void ApplyTaskbar(bool want)
    {
        try
        {
            if (want && !settings.TaskbarChanged && !Native.TaskbarAutoHide)
            {
                Native.TaskbarAutoHide = true;
                settings.TaskbarChanged = true;
                settings.Save();
                Log("Taskbar set to hide (OLED mode)");
            }
            else if (!want && settings.TaskbarChanged)
            {
                Native.TaskbarAutoHide = false;
                settings.TaskbarChanged = false;
                settings.Save();
                Log("Taskbar back to always showing");
            }
        }
        catch { }
    }

    static FlowLayoutPanel Row()
    {
        return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
    }

    // Pixel sizes grow with the Windows text-size (DPI) setting.
    int S(int px) { return (int)Math.Round(px * dpiScale); }

    void ToggleTheme()
    {
        Theme.Dark = !Theme.Dark;
        settings.Dark = Theme.Dark ? 1 : 0;
        settings.Save();
        Retheme();
    }

    void Retheme()
    {
        SuspendLayout();
        Theme.Apply(this, Theme.Back);
        UpdateTimeRows();
        UpdateWowRows();
        ResumeLayout();
        btnTheme.Text = Theme.Dark ? "Light mode" : "Dark mode";
        Theme.TitleBar(this);
        if (warning != null) warning.Retheme();
        if (oledForm != null && !oledForm.IsDisposed) oledForm.Retheme();
        if (wowWarning != null) wowWarning.Retheme();
        UpdateOledText();
        OnTick();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
        try
        {
            var displayState = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47"); // GUID_CONSOLE_DISPLAY_STATE
            Native.RegisterPowerSettingNotification(Handle, ref displayState, 0);
        }
        catch { }
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
        menuWow = new ToolStripMenuItem("Close WoW when not used", null, delegate { chkWow.Checked = !chkWow.Checked; });
        menu.Items.Add(menuWow);
        menu.Items.Add(new ToolStripSeparator());
        menuNight = new ToolStripMenuItem("Turn night light on", null, delegate { NightSwitch(!night.On, true, "you, from the tray menu"); });
        menu.Items.Add(menuNight);
        menuNightAuto = new ToolStripMenuItem("Night light by itself", null, delegate { chkNightAuto.Checked = !chkNightAuto.Checked; });
        menu.Items.Add(menuNightAuto);
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
        decimal per = settings.RandomInHours ? 60m : 1m;
        int rMin = Math.Max(1, (int)Math.Round(numRandMin.Value * per)), rMax = Math.Max(1, (int)Math.Round(numRandMax.Value * per));
        bool rangeChanged = rMin != settings.RandomMinMinutes || rMax != settings.RandomMaxMinutes;
        settings.RandomMinMinutes = rMin;
        settings.RandomMaxMinutes = rMax;
        if (rangeChanged) PickRandom();
        settings.Warn = chkWarn.Checked;
        settings.Beep = chkBeep.Checked;
        settings.CountMouse = chkMouse.Checked;
        settings.CountKeyboard = chkKeys.Checked;
        settings.CountController = chkPad.Checked;
        settings.WowClose = chkWow.Checked;
        settings.WowAfk = chkWowAfk.Checked;
        settings.WowAfkMinutes = (int)numWowAfk.Value;
        settings.WowLogin = chkWowLogin.Checked;
        settings.WowLoginMinutes = (int)numWowLogin.Value;
        menuWow.Checked = settings.WowClose;
        settings.NightAuto = chkNightAuto.Checked;
        menuNightAuto.Checked = settings.NightAuto;
        settings.Save();
        OnTick();
    }

    void ResetIdle()
    {
        armedTick = Native.GetTickCount();
        lastPadTick = armedTick;
        screenArmTick = armedTick;
        wowLoginSince.Clear(); // someone is here (or the PC just woke up): the login-screen time starts again
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
        // This runs on a Windows helper thread, so only leave a note; the timer does the real work.
        if (e.Mode == PowerModes.Resume) resumeFlag = true;
    }

    void OnTick()
    {
        if (timer == null) return;

        if (sleeping)
        {
            bool timedOut = Native.GetTickCount() - suspendStartTick > 20000u;
            if (!resumeFlag && !timedOut) return; // still going down
            if (timedOut && !resumeFlag) Log("Did not go down in 20 seconds; counting again");
        }
        if (resumeFlag)
        {
            resumeFlag = false;
            if (failedSuspend)
                tray.ShowBalloonTip(6000, "Idle Sleep", "Windows would not " + ActionPhrases[settings.Action] + ". See IdleSleep-log.txt.", ToolTipIcon.Warning);
            else Log("Woke up");
            failedSuspend = false;
            sleeping = false;
            ResetIdle();
        }
        else if (sleeping)
        {
            sleeping = false;
            ResetIdle();
        }

        var lii = new Native.LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);
        bool gotInput = Native.GetLastInputInfo(ref lii);
        uint now = Native.GetTickCount();
        if (unlockFlag)
        {
            // Back from the lock screen: start the screen timers fresh.
            unlockFlag = false;
            lockOffDone = false;
            screenArmTick = now;
            screenSeen.Clear();
        }

        uint idleMs = now - armedTick;
        string cause = "";
        uint mouseMs = Since(now, InputWatcher.LastMouse), keyMs = Since(now, InputWatcher.LastKey);
        if (settings.CountMouse) Use(ref idleMs, ref cause, mouseMs, "mouse");
        if (settings.CountKeyboard) Use(ref idleMs, ref cause, keyMs, "keyboard");
        // Screen care always counts every kind of use, whatever is ticked.
        uint screenIdle = Math.Min(now - screenArmTick, Math.Min(mouseMs, keyMs));
        // Real use only (not the start of the program, waking up or Cancel): the night light's "your day".
        uint realMs = uint.MaxValue;
        if (InputWatcher.LastMouse != InputWatcher.StartTick) realMs = Math.Min(realMs, mouseMs);
        if (InputWatcher.LastKey != InputWatcher.StartTick) realMs = Math.Min(realMs, keyMs);
        if (gotInput)
        {
            // Windows' own "last input" time also moves for things that are not a person: fake input from
            // programs, touchpads, some drivers. Only trust it when the watcher cannot see, which is when a
            // program running as administrator is in front (Windows hides its input from normal programs).
            uint sys = Since(now, lii.dwTime);
            uint seen = Math.Min(Math.Min(mouseMs, keyMs), Since(now, InputWatcher.LastIgnored));
            if (sys + 1500 < seen)
            {
                string front;
                if (FrontIsHidden(out front))
                {
                    if (settings.CountMouse && settings.CountKeyboard)
                        Use(ref idleMs, ref cause, sys, "mouse or keyboard in " + front + " (runs as admin)");
                    screenIdle = Math.Min(screenIdle, sys);
                    realMs = Math.Min(realMs, sys);
                }
                else if (unseenLogTick == 0 || now - unseenLogTick > 1800000u)
                {
                    unseenLogTick = now;
                    Log("Windows says the PC was used, but no real mouse or keyboard was seen, so it does not count (in front: " + front + ")");
                }
            }
        }
        PollControllers(now);
        if (settings.CountController) Use(ref idleMs, ref cause, now - lastPadTick, "controller");
        screenIdle = Math.Min(screenIdle, now - lastPadTick);
        if (lastPadRealTick != 0) realMs = Math.Min(realMs, Since(now, lastPadRealTick));
        if (cause != "") lastCause = cause;
        ScreenCare(now, screenIdle);
        NightTick(realMs); // works whether the big button is on or off

        if (!settings.Enabled)
        {
            HideWarning();
            HideWowWarning();
            wowLoginSince.Clear();
            lblStatus.Text = "Turned off. Nothing will happen to the PC.\n ";
            lblStatus.ForeColor = Theme.Sub;
            SetTray(false, "Idle Sleep: off");
            return;
        }

        lblStatus.ForeColor = Theme.Text;
        CheckWow(now, idleMs);

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
            lblStatus.Text = string.Format("Not used for {0}.  Random time: it will happen later.", Fmt(idleMs)) + CauseText(idleMs);
            SetTray(true, "Idle Sleep: on (random time)");
            return;
        }
        if (!settings.CountMouse && !settings.CountKeyboard && !settings.CountController)
            lblStatus.Text = string.Format("Nothing is ticked, so it will {0} in {1} no matter what.", ActionPhrases[settings.Action], Fmt(leftMs)) + "\n ";
        else
            lblStatus.Text = string.Format("Not used for {0}.  Will {1} in {2}.", Fmt(idleMs), ActionPhrases[settings.Action], Fmt(leftMs)) + CauseText(idleMs);
        SetTray(true, "Idle Sleep: " + ActionNames[settings.Action].ToLower() + " in " + FmtShort(leftMs));
    }

    // OLED mode: a black screen saver and/or switching the screen off after a while with no use.
    // If the screen comes back on while still nobody is using the PC (a program can do that), it is
    // switched off again, at most once a minute.
    void ScreenCare(uint now, uint idle)
    {
        bool oled = settings.Oled;
        if (locked)
        {
            // Typing a PIN on the lock screen cannot be seen from here, so only the one switch-off after locking.
            HideSaver();
            HideCovers();
            screenOffTick = 0;
            if (oled && settings.OffOnLock && !lockOffDone && now - lockTick >= 5000u)
            {
                lockOffDone = true;
                Log("Screen off (PC locked)");
                MonitorOff();
            }
            return;
        }
        BreakCheck(now, idle);
        ApplyTaskbar(oled && settings.HideTaskbar && idle >= (uint)settings.HideTaskbarMinutes * 60000u);
        bool saverDue = oled && settings.Saver && idle >= (uint)settings.SaverMinutes * 60000u;
        bool offDue = oled && settings.ScreenOff && idle >= (uint)settings.ScreenOffMinutes * 60000u;
        if (saverDue) ShowSaver(); else HideSaver();
        CoverUnused(now);
        if (!offDue) { screenOffTick = 0; return; }
        if (screenOffTick != 0 && (!displayOn || now - screenOffTick < 60000u)) return;
        Log(screenOffTick == 0 ? string.Format("Screen off (no use for {0} min)", settings.ScreenOffMinutes)
            : "Screen came back on with nobody using the PC; off again");
        screenOffTick = now == 0 ? 1 : now;
        MonitorOff();
    }

    void MonitorOff()
    {
        Native.SendMessage(Handle, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF170 /* SC_MONITORPOWER */, (IntPtr)2 /* off */);
    }

    // After the chosen hours of use without a 5 minute break, a reminder by the clock (again after the same time).
    void BreakCheck(uint now, uint idle)
    {
        if (idle >= 300000u) { useStartTick = 0; return; }
        if (useStartTick == 0) { useStartTick = now == 0 ? 1 : now; return; }
        if (!settings.Oled || !settings.BreakReminder || now - useStartTick < (uint)settings.BreakHours * 3600000u) return;
        useStartTick = now == 0 ? 1 : now;
        Log("Break reminder");
        tray.ShowBalloonTip(10000, "Time for a short break?",
            string.Format("You have used the PC for {0} hour{1}. A break rests your eyes, and your OLED screen too.",
                settings.BreakHours, settings.BreakHours == 1 ? "" : "s"), ToolTipIcon.Info);
    }

    // With 2 or more screens: black out a screen the mouse has not been on for the chosen time.
    void CoverUnused(uint now)
    {
        var screens = Screen.AllScreens;
        if (!settings.Oled || !settings.CoverUnused || savers != null || screens.Length < 2) { HideCovers(); return; }
        string here = Screen.FromPoint(Cursor.Position).DeviceName;
        foreach (var scr in screens)
        {
            string name = scr.DeviceName;
            if (name == here || !screenSeen.ContainsKey(name)) screenSeen[name] = now;
            if (covers.ContainsKey(name) || now - screenSeen[name] < (uint)settings.CoverUnusedMinutes * 60000u) continue;
            var f = new SaverForm(scr);
            f.Show();
            covers[name] = f;
            Log("Blacked out a screen the mouse was not on (" + name.TrimStart('\\', '.') + ")");
        }
    }

    void HideCovers()
    {
        foreach (var f in covers.Values) { f.Close(); f.Dispose(); }
        covers.Clear();
    }

    // Runs 10 times a second.
    void FastTick()
    {
        uint now = Native.GetTickCount();
        // Night light: fade towards on or off; while on, every 3 seconds put the warmth back if a game or
        // Windows has reset the screen colours.
        if (NightFilter.Level != nightTarget)
        {
            NightFilter.Level = nightTarget > NightFilter.Level
                ? Math.Min(nightTarget, NightFilter.Level + nightStep)
                : Math.Max(nightTarget, NightFilter.Level - nightStep);
            NightFilter.Apply();
            nightCheckTick = now;
        }
        else if (NightFilter.Level > 0 && now - nightCheckTick >= 3000u)
        {
            NightFilter.Apply();
            nightCheckTick = now;
        }
        if (NightFilter.BasesChanged) { NightFilter.BasesChanged = false; night.Save(); }
        if (savers != null)
        {
            // The first half second only notes where things stand (the pointer nudge may still be arriving).
            if (now - saverShownTick < 500) { saverMouse = InputWatcher.LastMouse; saverKey = InputWatcher.LastKey; }
            else if (InputWatcher.LastMouse != saverMouse || InputWatcher.LastKey != saverKey) { HideSaver(); OnTick(); }
        }
        // The pointer hides when the mouse has been still for a while, and comes back on the first move or click.
        if (settings.Oled && settings.HidePointer && !locked && savers == null
                && Since(now, InputWatcher.LastMouse) >= (uint)settings.HidePointerSeconds * 1000u)
            PointerHider.Hide();
        else PointerHider.Show();
        // The mouse moved onto a blacked-out screen: show it again.
        if (covers.Count > 0)
        {
            string here = Screen.FromPoint(Cursor.Position).DeviceName;
            SaverForm f;
            if (covers.TryGetValue(here, out f))
            {
                covers.Remove(here);
                f.Close();
                f.Dispose();
                screenSeen[here] = now;
            }
        }
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // A Windows helper thread: only leave a note for the timers.
        if (e.Reason == SessionSwitchReason.SessionLock) { lockTick = Native.GetTickCount(); locked = true; }
        else if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; unlockFlag = true; }
    }

    void ShowSaver()
    {
        if (savers != null) return;
        HideCovers();
        var screens = Screen.AllScreens;
        savers = new SaverForm[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            savers[i] = new SaverForm(screens[i]);
            savers[i].Show();
        }
        // Nudge the pointer one pixel and back so Windows hides it now, not on the next real move.
        // (Too small to count as use.)
        var p = Cursor.Position;
        Cursor.Position = new Point(p.X + 1, p.Y);
        Cursor.Position = p;
        saverShownTick = Native.GetTickCount();
        saverMouse = InputWatcher.LastMouse;
        saverKey = InputWatcher.LastKey;
    }

    void HideSaver()
    {
        if (savers == null) return;
        foreach (var f in savers) { f.Close(); f.Dispose(); }
        savers = null;
    }

    // Windows reports when the screen goes off or comes back on.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0218 /* WM_POWERBROADCAST */ && (int)m.WParam == 0x8013 /* PBT_POWERSETTINGCHANGE */)
            displayOn = Marshal.ReadInt32(m.LParam, 20) != 0; // after the 16-byte id and 4-byte length: 0 = off
        base.WndProc(ref m);
    }

    // Is the window in front a program whose input Idle Sleep cannot see? Remembered per program.
    bool FrontIsHidden(out string name)
    {
        uint pid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        if (pid != frontPid)
        {
            frontPid = pid;
            frontName = "the desktop";
            frontHidden = false;
            if (pid != 0)
            {
                IntPtr p = Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (p == IntPtr.Zero) { frontName = "a protected program"; frontHidden = !selfElevated; }
                else
                {
                    try
                    {
                        var sb = new StringBuilder(1024);
                        int len = sb.Capacity;
                        if (Native.QueryFullProcessImageName(p, 0, sb, ref len)) frontName = Path.GetFileName(sb.ToString());
                        frontHidden = !selfElevated && Native.IsElevated(p);
                    }
                    finally { Native.CloseHandle(p); }
                }
            }
        }
        name = frontName;
        return frontHidden;
    }

    static void Use(ref uint idleMs, ref string cause, uint sinceMs, string what)
    {
        if (sinceMs < idleMs) { idleMs = sinceMs; cause = what; }
    }

    // "  Last use: mouse." for a few minutes after it happened, so it is clear what keeps it awake.
    string CauseText(uint idleMs)
    {
        return lastCause == "" || idleMs > 300000u ? "\n " : "\nLast use: " + lastCause + ".";
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
                if (padSeen[i] && PadActive(padPrev[i], g, st.dwPacketNumber != padPacket[i])) lastPadTick = lastPadRealTick = now == 0 ? 1 : now;
                padPrev[i] = g;
                padPacket[i] = st.dwPacketNumber;
                padSeen[i] = true;
            }
        }
        catch { padApiOk = false; }
    }

    // Ignore tiny stick drift so a resting controller does not keep the PC awake. A stick held far over
    // only counts while the controller keeps reporting changes; a stuck reading that never changes does not.
    static bool PadActive(Native.XINPUT_GAMEPAD a, Native.XINPUT_GAMEPAD b, bool changed)
    {
        if (a.wButtons != b.wButtons) return true;
        if (Math.Abs(a.bLeftTrigger - b.bLeftTrigger) > 30 || Math.Abs(a.bRightTrigger - b.bRightTrigger) > 30) return true;
        return StickMoved(a.sThumbLX, b.sThumbLX, changed) || StickMoved(a.sThumbLY, b.sThumbLY, changed)
            || StickMoved(a.sThumbRX, b.sThumbRX, changed) || StickMoved(a.sThumbRY, b.sThumbRY, changed);
    }

    static bool StickMoved(short before, short after, bool changed)
    {
        return (changed && Math.Abs((int)after) > 8000) || Math.Abs(after - before) > 4000;
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

    // ---- Closing World of Warcraft when nobody uses it ----

    WarningForm wowWarning;
    List<int> wowPids = new List<int>();                               // WoW games open right now
    HashSet<int> wowOnline = new HashSet<int>();                       // ...of those, connected to a game server
    Dictionary<int, uint> wowLoginSince = new Dictionary<int, uint>(); // when each was first seen at the login screen
    Dictionary<int, uint> wowCloseSent = new Dictionary<int, uint>();  // when each was asked to close
    uint wowScanTick, lastWowUseTick = Native.GetTickCount() - 0x7FFFFFFFu;
    bool wowScanned;

    // WoW game windows: WoW.exe, Wow-64.exe, WowClassic.exe and so on (not launchers or helpers).
    static bool IsWowName(string name)
    {
        string n = name.ToLowerInvariant();
        return n.StartsWith("wow") && n.IndexOf("launcher") < 0 && n.IndexOf("voice") < 0
            && n.IndexOf("error") < 0 && n.IndexOf("crash") < 0;
    }

    void UpdateWowRows()
    {
        Color c = chkWow.Checked ? Theme.Text : Theme.Sub;
        foreach (Control row in wowRows.Controls)
        {
            if (row is Label) continue; // the grey note keeps its colour
            foreach (Control x in row.Controls) if (!(x is NumericUpDown)) x.ForeColor = c;
        }
    }

    // Every 5 seconds: which WoW games are open, and which of them sit at the login screen.
    void ScanWow(uint now)
    {
        wowScanTick = now;
        wowScanned = true;
        wowPids.Clear();
        foreach (var p in Process.GetProcesses())
        {
            try { if (IsWowName(p.ProcessName) && p.MainWindowHandle != IntPtr.Zero) wowPids.Add(p.Id); }
            catch { }
            finally { p.Dispose(); }
        }
        wowOnline = wowPids.Count > 0 ? Net.PidsOnline() : new HashSet<int>();
        foreach (int pid in new List<int>(wowLoginSince.Keys)) if (!wowPids.Contains(pid)) wowLoginSince.Remove(pid);
        foreach (int pid in wowPids)
        {
            if (wowOnline.Contains(pid)) wowLoginSince.Remove(pid);
            else if (!wowLoginSince.ContainsKey(pid)) wowLoginSince[pid] = now;
        }
    }

    void CheckWow(uint now, uint idleMs)
    {
        // A WoW that was asked to close but is still open after 30 seconds gets ended.
        foreach (int pid in new List<int>(wowCloseSent.Keys))
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) wowCloseSent.Remove(pid);
                    else if (now - wowCloseSent[pid] > 30000u)
                    {
                        Log(p.ProcessName + " did not close in 30 seconds, so it was ended");
                        wowCloseSent.Remove(pid);
                        p.Kill();
                    }
                }
            }
            catch (ArgumentException) { wowCloseSent.Remove(pid); } // already gone
            catch (Exception ex) { wowCloseSent.Remove(pid); Log("Could not end WoW: " + ex.Message); }
        }

        if (!settings.WowClose || (!settings.WowAfk && !settings.WowLogin))
        {
            HideWowWarning();
            wowLoginSince.Clear();
            return;
        }
        if (!wowScanned || now - wowScanTick >= 5000u) ScanWow(now);
        if (wowPids.Count == 0) { HideWowWarning(); return; }

        // Using the PC while a WoW window is in front counts as using WoW (for the login-screen rule;
        // using other programs does not stop a forgotten, disconnected WoW from closing).
        uint fpid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fpid);
        if (idleMs < 2000u && wowPids.Contains((int)fpid)) lastWowUseTick = now;

        uint afkLimit = (uint)settings.WowAfkMinutes * 60000u, loginLimit = (uint)settings.WowLoginMinutes * 60000u;
        uint soonest = uint.MaxValue;
        bool soonestLogin = false;
        foreach (int pid in wowPids.ToArray())
        {
            if (wowCloseSent.ContainsKey(pid)) continue;
            uint left = uint.MaxValue, since;
            bool byLogin = false;
            if (settings.WowAfk) left = idleMs >= afkLimit ? 0 : afkLimit - idleMs;
            if (settings.WowLogin && wowLoginSince.TryGetValue(pid, out since))
            {
                uint atLogin = Math.Min(Since(now, since), Since(now, lastWowUseTick));
                uint l = atLogin >= loginLimit ? 0 : loginLimit - atLogin;
                if (l < left) { left = l; byLogin = true; }
            }
            if (left == 0)
            {
                CloseWow(pid, byLogin ? string.Format("{0} min at the login screen", settings.WowLoginMinutes)
                                      : string.Format("no use for {0} min", settings.WowAfkMinutes));
                continue;
            }
            if (left < soonest) { soonest = left; soonestLogin = byLogin; }
        }
        if (soonest <= 60000u) ShowWowWarning(soonest, soonestLogin);
        else HideWowWarning();
    }

    // Close it like clicking the X; if it is still open 30 seconds later, CheckWow ends it.
    void CloseWow(int pid, string reason)
    {
        try
        {
            using (var p = Process.GetProcessById(pid))
            {
                Log("Closing " + p.ProcessName + " (" + reason + ")");
                wowCloseSent[pid] = Native.GetTickCount();
                if (!p.CloseMainWindow()) { wowCloseSent.Remove(pid); p.Kill(); }
            }
        }
        catch (Exception ex) { Log("Could not close WoW: " + ex.Message); }
        wowLoginSince.Remove(pid);
        HideWowWarning();
    }

    void ShowWowWarning(uint leftMs, bool byLogin)
    {
        if (wowWarning == null)
        {
            wowWarning = new WarningForm(iconOn) { Lead = "WoW will", Phrase = "close" };
            wowWarning.StayAwake += delegate { lastWowUseTick = Native.GetTickCount(); ResetIdle(); HideWowWarning(); OnTick(); };
        }
        wowWarning.Hint = byLogin ? "WoW is at the login screen. Click Cancel to keep it open."
                                  : "Move the mouse to stop it, or click the button.";
        wowWarning.SetSeconds((int)Math.Ceiling(leftMs / 1000.0));
        if (!wowWarning.Visible) wowWarning.Show();
        wowWarning.PlaceBottomRight(warning != null && warning.Visible ? warning.Height + 10 : 0);
    }

    void HideWowWarning()
    {
        if (wowWarning != null && wowWarning.Visible) wowWarning.Hide();
    }

    // ---- Night light (Idle Sleep's own warmer colours) ----

    NightPlan night = NightPlan.Load();
    double nightTarget, nightStep = 1 / 15.0; // fade towards 0 (off) or 1 (on), this much every 0.1 s
    uint nightCheckTick, nightSaveTick;
    Button btnNight;
    CheckBox chkNightAuto;
    TrackBar trkNight;
    Label lblNight, lblNightLearned, lblNightStrength;
    ToolStripMenuItem menuNight, menuNightAuto;

    static readonly TimeSpan NightBreak = TimeSpan.FromHours(5);   // a break this long can start a new day...
    static readonly TimeSpan AwakeBefore = TimeSpan.FromHours(10); // ...if you had been up this long before it,
    static readonly TimeSpan OldDay = TimeSpan.FromHours(20);      // or the day was this old anyway
    const int DefaultOnMinutes = 12 * 60;                          // until it has learned: 12 hours into your day

    static DateTime Utc(long ticks) { return new DateTime(ticks, DateTimeKind.Utc); }

    int OnMinutes() { int m = NightPlan.Average(night.OnTimes); return m < 0 ? DefaultOnMinutes : m; }

    // Every second, with how long ago the PC was really used (mouse, keyboard or controller).
    void NightTick(uint realMs)
    {
        DateTime now = DateTime.UtcNow;
        if (realMs < 3000u) NightUse(now - TimeSpan.FromMilliseconds(realMs));
        if (settings.NightAuto && night.DayStart != 0)
        {
            TimeSpan into = now - Utc(night.DayStart);
            int len = NightPlan.Average(night.Lengths);
            if (!night.On && night.FirstOnAt == 0 && night.NoAutoOnDay != night.DayStart
                    && into >= TimeSpan.FromMinutes(OnMinutes()) && into < TimeSpan.FromHours(24))
                NightSwitch(true, false, "by itself, " + Span(into) + " into your day");
            else if (night.On && night.FirstOnAt != 0 && len > 0 && night.NoAutoOffDay != night.DayStart
                    && now - Utc(night.FirstOnAt) >= TimeSpan.FromMinutes(len))
                NightSwitch(false, false, "by itself, after " + Span(TimeSpan.FromMinutes(len)));
        }
        UpdateNightText(now);
    }

    // The PC was used at this moment. Coming back after a long break (like a night's sleep) starts a new day,
    // unless the break came early in the day (out at work): then it is the same day carrying on.
    void NightUse(DateTime at)
    {
        if (night.LastUse != 0)
        {
            DateTime last = Utc(night.LastUse);
            TimeSpan away = at - last;
            if (away >= NightBreak)
            {
                DateTime start = Utc(night.DayStart);
                if (night.DayStart == 0 || last - start >= AwakeBefore || at - start >= OldDay) NewDay(at, away);
            }
        }
        if (at.Ticks > night.LastUse) night.LastUse = at.Ticks;
        uint tick = Native.GetTickCount();
        if (nightSaveTick == 0 || tick - nightSaveTick >= 60000u) { nightSaveTick = tick == 0 ? 1 : tick; night.Save(); }
    }

    void NewDay(DateTime at, TimeSpan away)
    {
        Log("Night light: new day (back after " + Span(away) + " away)");
        night.DayStart = at.Ticks;
        night.FirstOnAt = 0;
        night.FirstOnWasAuto = false;
        if (night.On && settings.NightAuto) NightSwitch(false, false, "by itself, a new day started");
        night.Save();
    }

    // Turns the night light on or off. byYou = the button or the tray menu: that is what it learns from.
    void NightSwitch(bool on, bool byYou, string why)
    {
        if (on == night.On) return;
        DateTime now = DateTime.UtcNow;
        string learned = "";
        if (on)
        {
            if (night.FirstOnAt == 0)
            {
                night.FirstOnAt = now.Ticks;
                night.FirstOnWasAuto = !byYou;
                TimeSpan into = now - Utc(night.DayStart);
                if (byYou && night.DayStart != 0 && into >= TimeSpan.FromMinutes(30) && into < TimeSpan.FromHours(24))
                {
                    night.Add(night.OnTimes, ref night.OnTimesDay, (int)into.TotalMinutes);
                    learned = "; learned: on " + Span(into) + " into your day";
                }
            }
            // On again later the same day: it stays on until you turn it off or your next day starts.
            else if (byYou) night.NoAutoOffDay = night.DayStart;
        }
        else if (byYou)
        {
            if (night.FirstOnAt != 0)
            {
                TimeSpan len = now - Utc(night.FirstOnAt);
                if (night.FirstOnWasAuto && len < TimeSpan.FromMinutes(15))
                {
                    // Turned off just after it came on by itself: too early. It waits for you today,
                    // and learns the time you turn it on yourself.
                    night.FirstOnAt = 0;
                    learned = "; too early, so it waits for you today";
                }
                else if (len >= TimeSpan.FromMinutes(5))
                {
                    night.Add(night.Lengths, ref night.LengthsDay, (int)Math.Min(1440, len.TotalMinutes));
                    learned = "; learned: on for " + Span(len);
                }
            }
            night.NoAutoOnDay = night.DayStart; // it does not come back on by itself today
        }
        night.On = on;
        nightTarget = on ? 1 : 0;
        // By hand it changes in 1.5 seconds; by itself it fades over a minute, so it is no jolt in a game.
        nightStep = byYou ? 1 / 15.0 : 1 / 600.0;
        night.Save();
        Log("Night light " + (on ? "on" : "off") + " (" + why + learned + ")");
        UpdateNightText(now);
    }

    void NightStrengthChanged()
    {
        lblNightStrength.Text = trkNight.Value + "%";
        if (loading) return;
        settings.NightStrength = trkNight.Value;
        NightFilter.Strength = trkNight.Value;
        NightFilter.StrengthChanged();
        if (NightFilter.Level > 0) NightFilter.Apply();
        settings.Save();
    }

    void UpdateNightText(DateTime now)
    {
        string b = night.On ? "Turn night light off" : "Turn night light on";
        if (btnNight.Text != b) btnNight.Text = b;
        if (menuNight.Text != b) menuNight.Text = b;
        lblNightStrength.Text = trkNight.Value + "%";

        int len = NightPlan.Average(night.Lengths);
        string s;
        if (!settings.NightAuto)
            s = night.On ? "On. Only by hand (tick the box to let it go off by itself)." : "Only by hand. Tick the box to let it come on by itself.";
        else if (night.DayStart == 0)
            s = night.On ? "On. Learning starts after your first long break (5+ hours, like a night's sleep)."
                         : "Learning starts after your first long break (5+ hours, like a night's sleep).";
        else if (night.On)
        {
            if (night.FirstOnAt != 0 && len > 0 && night.NoAutoOffDay != night.DayStart)
            {
                TimeSpan left = Utc(night.FirstOnAt).AddMinutes(len) - now;
                s = left.TotalMinutes >= 1 ? "On. Goes off by itself in about " + Span(left) + "." : "On. Going off now.";
            }
            else s = "On until you turn it off, or your next day starts.";
        }
        else if (night.FirstOnAt == 0 && night.NoAutoOnDay != night.DayStart)
        {
            TimeSpan left = Utc(night.DayStart).AddMinutes(OnMinutes()) - now;
            if (now - Utc(night.DayStart) >= TimeSpan.FromHours(24)) s = "Off. Waiting for your next day to start.";
            else s = left.TotalMinutes >= 1 ? "Off. Comes on by itself in about " + Span(left) + "." : "Off. Coming on now.";
        }
        else s = "Off for the rest of today. It comes on again in your next day.";
        if (NightFilter.Limited && night.On) s += "\nA screen won't go this warm, so it is as warm as it allows.";
        if (lblNight.Text != s) lblNight.Text = s;

        string l = "Comes on " + Span(TimeSpan.FromMinutes(OnMinutes())) + " into your day"
            + (night.OnTimes.Count == 0 ? " (not learned yet)." : " (learned " + Times(night.OnTimes.Count) + ").")
            + "\nStays on " + (len <= 0 ? "until your next day starts (not learned yet)."
                                        : Span(TimeSpan.FromMinutes(len)) + " (learned " + Times(night.Lengths.Count) + ").");
        if (lblNightLearned.Text != l) lblNightLearned.Text = l;
    }

    static string Times(int n) { return "from " + n + (n == 1 ? " time" : " times"); }

    // "45 min", "3 h", "12 h 30 min".
    static string Span(TimeSpan t)
    {
        int mins = Math.Max(0, (int)Math.Round(t.TotalMinutes));
        if (mins < 60) return mins + " min";
        return (mins / 60) + " h" + (mins % 60 == 0 ? "" : " " + (mins % 60) + " min");
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
        suspendStartTick = Native.GetTickCount();
        try
        {
            Native.EnableShutdownPrivilege();
            uint force = settings.ForceClose ? 0x4u : 0u; // EWX_FORCE
            if (a == 0 || a == 1)
            {
                // Sleep/hibernate does not come back until the PC wakes up, so it runs on its own thread.
                // That way this window never gets stuck waiting, and the timer keeps working after waking.
                bool hib = a == 1;
                var t = new Thread(delegate ()
                {
                    bool ok2 = false;
                    try { ok2 = Native.SetSuspendState(hib, false, false); }
                    catch (Exception ex) { Log("Sleep call failed: " + ex.Message); }
                    if (!ok2) { Log("Windows refused: " + ActionNames[a] + ", error " + Marshal.GetLastWin32Error()); failedSuspend = true; }
                    resumeFlag = true; // the call returned: wake up and start counting again
                });
                t.IsBackground = true;
                t.Start();
                return; // 'sleeping' is cleared by OnTick once the PC has woken (or after a short time-out)
            }
            bool ok;
            switch (a)
            {
                case 2: ok = Native.ExitWindowsEx(0x8u | force, 0x80000000u); break; // EWX_POWEROFF
                case 3: ok = Native.ExitWindowsEx(0x2u | force, 0x80000000u); break; // EWX_REBOOT
                default: ok = Native.LockWorkStation(); break;
            }
            int err = Marshal.GetLastWin32Error();
            if (!ok)
            {
                Log("Windows refused: " + ActionNames[a] + ", error " + err);
                tray.ShowBalloonTip(6000, "Idle Sleep", "Windows would not " + ActionPhrases[a] + " (error " + err + ").", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log("Action failed: " + ex.Message);
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

    public void ShowFromTray()
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
        if (e.CloseReason == CloseReason.WindowsShutDown) windowsEnding = true;
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
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        timer.Stop();
        fastTimer.Stop();
        HideSaver();
        HideCovers();
        PointerHider.Show();
        // Exit puts the taskbar back; when Windows shuts down it stays as it is, ready for the next start.
        if (!windowsEnding) ApplyTaskbar(false);
        // Normal colours while Idle Sleep is not running; it remembers whether the night light was on.
        NightFilter.Restore();
        night.Save();
        tray.Visible = false;
        tray.Dispose();
        if (warning != null) warning.Dispose();
        if (wowWarning != null) wowWarning.Dispose();
        base.OnFormClosed(e);
    }

    // Start with Windows. Idle Sleep runs as administrator (so it can see the mouse and keyboard inside
    // games that run as administrator), and Windows will not start such a program from the normal startup
    // list, so it uses a Task Scheduler task that starts it at log on without asking.
    const string TaskName = "Idle Sleep";

    static bool StartupTaskExists() { return Schtasks("/query /tn \"" + TaskName + "\"") == 0; }

    static bool StartupRunKeyExists()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }
        catch { return false; }
    }

    static bool GetStartup() { return StartupTaskExists() || StartupRunKeyExists(); }

    // Older versions used the normal startup list; move that over to the task.
    // Tasks made by older versions start with no delay; remake them with the delay.
    static void MoveOldStartup()
    {
        if (!selfElevated) return;
        if (StartupRunKeyExists()) { SetStartup(true); return; }
        string xml;
        if (Schtasks("/query /tn \"" + TaskName + "\" /xml", out xml) == 0 && xml.IndexOf("<Delay>" + TaskDelay + "</Delay>", StringComparison.Ordinal) < 0)
            SetStartup(true);
    }

    const string TaskDelay = "PT30S";

    static void SetStartup(bool on)
    {
        try
        {
            if (!selfElevated)
            {
                // Not running as administrator (should not happen): fall back to the normal startup list.
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" /tray");
                    else k.DeleteValue(RunName, false);
                }
                return;
            }
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (k != null) k.DeleteValue(RunName, false);
            int code;
            if (on)
            {
                string xml = Path.Combine(Path.GetTempPath(), "IdleSleep-task.xml");
                File.WriteAllText(xml, TaskXml(), Encoding.Unicode);
                code = Schtasks("/create /tn \"" + TaskName + "\" /xml \"" + xml + "\" /f");
                try { File.Delete(xml); } catch { }
            }
            else code = StartupTaskExists() ? Schtasks("/delete /tn \"" + TaskName + "\" /f") : 0;
            if (code != 0) throw new Exception("Task Scheduler said no (code " + code + ").");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not change the startup setting:\n" + ex.Message, "Idle Sleep");
        }
    }

    static string TaskXml()
    {
        string user = SecurityElement.Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(Application.ExecutablePath);
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo><Description>Starts Idle Sleep when you log on.</Description></RegistrationInfo>\r\n" +
            // Wait 30 seconds after log on so the taskbar is ready for the moon icon.
            "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>" + TaskDelay + "</Delay></LogonTrigger></Triggers>\r\n" +
            "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
            "  <Settings>\r\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
            "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
            "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
            "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
            "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
            "    <Enabled>true</Enabled>\r\n" +
            "    <Hidden>false</Hidden>\r\n" +
            "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
            "    <Priority>4</Priority>\r\n" +
            "  </Settings>\r\n" +
            "  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>/tray</Arguments></Exec></Actions>\r\n" +
            "</Task>\r\n";
    }

    static int Schtasks(string args)
    {
        string output;
        return Schtasks(args, out output);
    }

    static int Schtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            output = p.StandardOutput.ReadToEnd();
            err.Wait();
            p.WaitForExit();
            return p.ExitCode;
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
