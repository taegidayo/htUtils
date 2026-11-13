using System.Runtime.InteropServices;

namespace HtUtil.Agent;

/// Admin-only session options (both off by default, toggled separately from the viewer). Must run on the UI thread.
///  Blank: black window on every monitor that only the people at this PC see — excluded from capture
///         (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+), click-through so remote clicks still reach the apps below.
///  Lock:  low-level hooks drop physical keyboard/mouse input; our SendInput events carry the INJECTED flag and pass.
///         Ctrl+Alt+Del can't be blocked (by Windows design), so the local user is never locked out for good.
static class Privacy
{
    static readonly List<Form> Covers = [];
    public static bool Locked => _kb != IntPtr.Zero;
    static string CoverText => "관리자가 원격으로 작업 중입니다" + (Locked ? "\n\n키보드·마우스 입력이 잠겨 있습니다" : "");
    static IntPtr _kb, _ms;
    static readonly HookProc KbProc = (n, w, l) => n >= 0 && (Marshal.ReadInt32(l, 8) & 0x10) == 0 ? 1 : CallNextHookEx(IntPtr.Zero, n, w, l); // LLKHF_INJECTED
    static readonly HookProc MsProc = (n, w, l) => n >= 0 && (Marshal.ReadInt32(l, 12) & 0x1) == 0 ? 1 : CallNextHookEx(IntPtr.Zero, n, w, l);  // LLMHF_INJECTED

    public static bool Blank(bool on)
    {
        foreach (var f in Covers) f.Close();
        Covers.Clear();
        if (!on) return true;
        foreach (var s in Screen.AllScreens)
        {
            var f = new Cover { Bounds = s.Bounds };
            f.Controls.Add(new Label { Text = CoverText, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 150, 150),
                TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Malgun Gothic", 20) });
            f.Show();
            if (!SetWindowDisplayAffinity(f.Handle, 0x11 /* WDA_EXCLUDEFROMCAPTURE */))
            {
                Log.Write("화면 가리기: 이 Windows 버전은 캡처 제외를 지원하지 않음 (Windows 10 2004 이상 필요)");
                f.Close(); Blank(false); return false; // otherwise the viewer would see black too
            }
            Covers.Add(f);
        }
        return true;
    }

    public static void Lock(bool on)
    {
        if (_kb != IntPtr.Zero) { UnhookWindowsHookEx(_kb); _kb = IntPtr.Zero; }
        if (_ms != IntPtr.Zero) { UnhookWindowsHookEx(_ms); _ms = IntPtr.Zero; }
        if (on)
        {
            var mod = GetModuleHandle(null);
            _kb = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, KbProc, mod, 0);
            _ms = SetWindowsHookEx(14 /* WH_MOUSE_LL */, MsProc, mod, 0);
        }
        foreach (var f in Covers) foreach (Control c in f.Controls) c.Text = CoverText; // blank screen shows the lock too
    }

    sealed class Cover : Form
    {
        public Cover()
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; BackColor = Color.Black;
            TopMost = true; ShowInTaskbar = false;
        }
        protected override bool ShowWithoutActivation => true;
        // TRANSPARENT+LAYERED = clicks fall through to the windows below; NOACTIVATE = never takes keyboard focus; TOOLWINDOW = no Alt+Tab
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x20 | 0x80000 | 0x8000000 | 0x80; return p; } }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SetLayeredWindowAttributes(Handle, 0, 255, 2 /* LWA_ALPHA */); }
    }

    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
}

/// Clipboard text both ways. Clipboard needs an STA thread, so each call runs on a short-lived one.
static class Clip
{
    // Read with plain Win32, not WinForms' Clipboard.GetText: that goes through OLE (COM calls into the copying app),
    // which can fail silently from the SYSTEM session agent (service mode) — writing worked, reading didn't.
    public static string? Get() => Sta(() =>
    {
        var open = false;
        for (var i = 0; i < 10 && !(open = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(20); // another app may hold it briefly
        if (!open) throw new Exception($"OpenClipboard 실패 ({Marshal.GetLastWin32Error()})");
        try
        {
            var h = GetClipboardData(13 /* CF_UNICODETEXT */);
            if (h == IntPtr.Zero) return null; // not text (image, files…)
            var p = GlobalLock(h);
            try { return p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    });
    public static void Set(string s) => Sta<object?>(() => { Clipboard.SetText(s); return null; });
    /// Files as a "cut" (Preferred DropEffect = MOVE): pasting in Explorer moves them instead of copying.
    public static void SetFiles(IEnumerable<string> paths) => Sta<object?>(() =>
    {
        var d = new DataObject();
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange(paths.ToArray());
        d.SetFileDropList(list);
        d.SetData("Preferred DropEffect", new MemoryStream([2, 0, 0, 0])); // DROPEFFECT_MOVE
        Clipboard.SetDataObject(d, true);
        return null;
    });
    public static uint Seq => GetClipboardSequenceNumber();

    static T? Sta<T>(Func<T> f)
    {
        T? r = default;
        var t = new Thread(() => { try { InputDesktop.Sync(); r = f(); } catch (Exception e) { Log.Write($"클립보드: {e.Message}"); } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join(2000);
        return r;
    }
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
}

/// Ctrl+Alt+Del: only a service may generate it, and only if the SoftwareSASGeneration policy allows
/// (the service sets it at start). User mode can't.
static class Sas
{
    public const string EventName = @"Global\HtUtilSAS";
    /// Session agent → service. false = not in service mode (no service listening).
    public static bool Request() { if (!EventWaitHandle.TryOpenExisting(EventName, out var ev)) return false; using (ev) ev.Set(); return true; }

    public static void EnablePolicy()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        if (k.GetValue("SoftwareSASGeneration") is not int v || (v & 1) == 0) k.SetValue("SoftwareSASGeneration", 3); // services + ease of access apps
    }
    [DllImport("sas.dll")] public static extern void SendSAS(bool asUser);
}
