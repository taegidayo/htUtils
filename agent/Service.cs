using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace HtUtil.Agent;

/// Lock screen / logon screen / UAC run on the secure "Winlogon" desktop, which a normal user process cannot capture
/// or inject into. So on install the agent becomes a LocalSystem service whose only job is to keep a SYSTEM copy of
/// the normal agent ("--session") running in the active console session. That copy follows the input desktop
/// (InputDesktop.Sync) and does everything else exactly like the user-mode agent.
sealed class AgentService : ServiceBase
{
    public const string Name = "HtUtilAgent";
    public const string ProcessName = "HtUtil.Agent";
    readonly string _exe = Environment.ProcessPath!;
    readonly object _lock = new();
    System.Threading.Timer? _timer;
    Process? _session;
    uint _sessionId = uint.MaxValue;
    bool _cleaned;

    public AgentService() { ServiceName = Name; CanStop = true; }

    protected override void OnStart(string[] args)
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, 3000, 2000);
        // Ctrl+Alt+Del for the viewer: SendSAS works from a service (not from the session agent), so the session agent
        // signals this event and we send it. Both run as SYSTEM, so the default security fits.
        new Thread(() =>
        {
            try
            {
                Sas.EnablePolicy();
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Sas.EventName);
                while (ev.WaitOne()) { Log.Write("Ctrl+Alt+Del 보냄"); Sas.SendSAS(false); }
            }
            catch (Exception e) { Log.Write($"Ctrl+Alt+Del 준비 실패: {e.Message}"); }
        }) { IsBackground = true }.Start();
    }

    protected override void OnStop() { _timer?.Dispose(); try { _session?.Kill(); } catch { } }

    void Tick()
    {
        lock (_lock)
        {
            try
            {
                // The web update swapped the exe on disk: die so the SCM recovery action restarts us from the new file.
                // Kill, not Environment.Exit: Exit from this timer thread left a 26.11.13.2 service running for good
                // (its renamed image then blocked the next update's .old delete).
                var fv = FileVersionInfo.GetVersionInfo(_exe);
                if (new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart) != Agent.Version) Process.GetCurrentProcess().Kill();

                if (!_cleaned) // once, 3s after start (the installer has exited by then)
                {
                    _cleaned = true;
                    try { Installer.SecureDataDir(); } catch { } // identity must be readable by the SYSTEM session agent
                    try { Installer.AllowFirewall(); } catch { } // PCs installed before the rule existed get it after a web update
                    // other agent copies (old session agents, a user-mode agent) would hold the single-instance mutex
                    Installer.KillOtherAgents();
                }

                var sid = WTSGetActiveConsoleSessionId();
                if (sid == 0xFFFFFFFF) return; // no console session right now (switching)
                if (_session is { HasExited: false } && _sessionId == sid) return;
                try { if (_session is { HasExited: false }) _session.Kill(); } catch { }
                _session = Native.StartAsSystemInSession(_exe, "--session", sid);
                _sessionId = sid;
            }
            catch (Exception e) { EventLog.WriteEntry("Application", $"HtUtil Agent: {e.Message}", EventLogEntryType.Warning); }
        }
    }

    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
}

static class Installer
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HtUtil");
    public static readonly string Exe = Path.Combine(Dir, "HtUtil.Agent.exe");
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HtUtil");

    /// Double-clicked exe: hand over to the service (installing/updating it with UAC). false = run in user mode instead.
    public static bool HandOffToService()
    {
        using var sc = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == AgentService.Name);
        if (sc is { Status: ServiceControllerStatus.Running } && !IsNewerThanInstalled())
        {
            MessageBox.Show("HtUtil Agent가 이미 서비스로 실행 중입니다.\n작업 표시줄 트레이의 파란 아이콘을 확인하세요.", Program.AppName);
            return true;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--install") { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
            if (p.ExitCode == 0) return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // UAC declined
        {
            MessageBox.Show("관리자 권한 없이 실행합니다.\n이 경우 잠금화면·로그인 화면·UAC 창에서는 원격 조작이 되지 않습니다.", Program.AppName);
        }
        return false;
    }

    static bool IsNewerThanInstalled()
    {
        if (!File.Exists(Exe)) return true;
        var fv = FileVersionInfo.GetVersionInfo(Exe);
        return Agent.Version > new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart);
    }

    /// Elevated: copy exe to Program Files, keep this PC's identity, (re)create and start the service.
    public static int Install()
    {
        try
        {
            using (var sc = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == AgentService.Name))
                if (sc is { Status: not ServiceControllerStatus.Stopped }) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
            KillOtherAgents();

            Directory.CreateDirectory(Dir);
            var self = Environment.ProcessPath!;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase))
            {
                // a running session agent may still have it open: a running exe can be renamed, not overwritten
                if (File.Exists(Exe)) { try { File.Delete(Exe + ".old"); } catch { } File.Move(Exe, Exe + ".old", true); }
                File.Copy(self, Exe);
                var cfg = Path.Combine(AppContext.BaseDirectory, "agent.json"); // optional override next to the exe
                if (File.Exists(cfg)) File.Copy(cfg, Path.Combine(Dir, "agent.json"), true);
            }

            // Same PC ID as the user-mode agent: move the installing user's identity; then only SYSTEM/Admins can read it.
            Directory.CreateDirectory(DataDir);
            var id = Path.Combine(DataDir, "identity.json");
            if (!File.Exists(id) && File.Exists(Agent.UserIdPath)) File.Copy(Agent.UserIdPath, id);
            var turn = Path.Combine(DataDir, "turn.json");
            if (!File.Exists(turn) && File.Exists(Agent.UserTurnPath)) File.Copy(Agent.UserTurnPath, turn);
            SecureDataDir();
            AllowFirewall();

            if (Run("sc", $"query {AgentService.Name}") != 0)
                Check(Run("sc", $"create {AgentService.Name} binPath= \"\\\"{Exe}\\\" --service\" start= auto DisplayName= \"HtUtil Agent\""), "서비스 등록");
            else Run("sc", $"config {AgentService.Name} binPath= \"\\\"{Exe}\\\" --service\" DisplayName= \"HtUtil Agent\"");
            Run("sc", $"description {AgentService.Name} \"HtUtil 원격 지원 에이전트 (잠금화면 포함)\"");
            Run("sc", $"failure {AgentService.Name} reset= 86400 actions= restart/3000/restart/3000/restart/3000");
            using (var sc = new ServiceController(AgentService.Name)) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show($"서비스 설치 실패: {e.Message}", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// Elevated: "HtUtil.Agent.exe --uninstall". Keeps the identity in ProgramData so a reinstall keeps the PC ID.
    public static int Uninstall()
    {
        try
        {
            Run("sc", $"stop {AgentService.Name}");
            Thread.Sleep(2000);
            Run("sc", $"delete {AgentService.Name}");
            Run("netsh", "advfirewall firewall delete rule name=\"HtUtil Agent\"");
            KillOtherAgents();
            Thread.Sleep(1000);
            if (Directory.Exists(Dir) && !Environment.ProcessPath!.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) Directory.Delete(Dir, true);
            MessageBox.Show("HtUtil Agent 서비스를 제거했습니다.", Program.AppName);
            return 0;
        }
        catch (Exception e) { MessageBox.Show($"제거 실패: {e.Message}", Program.AppName); return 1; }
    }

    /// ProgramData\HtUtil readable only by SYSTEM + Administrators, files inheriting from the folder.
    /// Also repairs the 26.11.12.x installer bug: "icacls … (OI)(CI) … /T" left identity.json with an EMPTY DACL
    /// (inherited ACEs removed, grants not applied to the file) → even SYSTEM got "Access denied".
    /// Runs elevated (install) and as SYSTEM (every service start), so already-broken PCs heal after an update.
    public static void SecureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        foreach (var f in Directory.GetFiles(DataDir)) Run("takeown", $"/F \"{f}\" /A"); // owner = Administrators → can rewrite the DACL
        Run("icacls", $"\"{DataDir}\" /reset /T /C /Q");                                // files: inherit only
        Run("icacls", $"\"{DataDir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /C /Q"); // folder only, no /T
    }

    /// Inbound allow rule for the installed exe. A SYSTEM service gets no "allow access?" prompt, so without this the
    /// firewall silently drops the browser's ICE checks (the browser hides its LAN IP behind mDNS, so the agent can't
    /// open the path first) → same-LAN remote fails toward a PC with the firewall on. Elevated / SYSTEM only.
    public static void AllowFirewall()
    {
        Run("netsh", "advfirewall firewall delete rule name=\"HtUtil Agent\"");
        Run("netsh", $"advfirewall firewall add rule name=\"HtUtil Agent\" dir=in action=allow program=\"{Exe}\" enable=yes profile=any");
    }

    /// Session agent, SYSTEM: a service process started before the current exe was written runs an old image (older
    /// services never restarted after a web update) → kill it; the SCM restarts it from the new exe.
    public static void KillStaleService()
    {
        var pid = ServicePid();
        if (pid == 0) return;
        using var p = Process.GetProcessById(pid);
        if (p.StartTime < File.GetLastWriteTime(Exe)) { Log.Write("옛 버전 서비스 재시작"); p.Kill(); }
    }

    static int ServicePid()
    {
        var psi = new ProcessStartInfo("sc", $"queryex {AgentService.Name}") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var m = System.Text.RegularExpressions.Regex.Match(p.StandardOutput.ReadToEnd(), @"PID\s*:\s*(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// Other agent copies (session agents, user-mode agents) hold the single-instance mutex.
    public static void KillOtherAgents()
    {
        foreach (var p in Process.GetProcessesByName(AgentService.ProcessName))
            if (p.Id != Environment.ProcessId) try { p.Kill(); } catch { }
    }

    static int Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        return p.ExitCode;
    }

    static void Check(int code, string what) { if (code != 0) throw new Exception($"{what} 실패 (코드 {code})"); }
}

/// Lock screen / UAC prompt = "Winlogon" input desktop. A SYSTEM thread can switch to it; call before capture/SendInput.
static class InputDesktop
{
    [ThreadStatic] static string? _current;
    static string _logged = "";

    /// Name of the desktop that currently gets input ("Default", "Winlogon"…), "?" if it can't be opened.
    public static string Current()
    {
        var h = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (h == IntPtr.Zero) return "?";
        try { return Name(h); } finally { CloseDesktop(h); }
    }

    /// Moves the calling thread to the input desktop and returns its name ("?" = can't open it: user mode on the lock
    /// screen). Windows refuses (error 170 ERROR_BUSY) once a thread owns any window or hook, so callers use a fresh
    /// dedicated thread per desktop (ScreenStream, Input) instead of shared pool threads.
    public static string Sync()
    {
        var h = OpenInputDesktop(0, false, 0x10000000 /* GENERIC_ALL */);
        if (h == IntPtr.Zero) { Note("?", $"입력 데스크톱을 열 수 없음 (오류 {Marshal.GetLastWin32Error()}) — 잠금화면·관리자 창은 서비스 모드에서만 보임"); return "?"; }
        var name = Name(h);
        if (name == _current) { CloseDesktop(h); return name; }
        if (!SetThreadDesktop(h)) { var err = Marshal.GetLastWin32Error(); CloseDesktop(h); Note(name, $"데스크톱 전환 실패: {name} (오류 {err})"); return name; }
        _current = name; // ponytail: the thread's previous desktop handle is not closed (one handle per lock/unlock per thread)
        Note(name, $"데스크톱: {name}");
        return name;
    }

    static void Note(string key, string msg) { if (_logged == key) return; _logged = key; Log.Write(msg); } // once per change, not per thread

    static string Name(IntPtr h)
    {
        var buf = new char[256];
        return GetUserObjectInformation(h, 2 /* UOI_NAME */, buf, buf.Length * 2, out var len) ? new string(buf, 0, Math.Max(0, len / 2 - 1)) : "";
    }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h, int index, [Out] char[] info, int length, out int needed);
}

static class Native
{
    /// Start exe as SYSTEM (our own token) inside the given session, on winsta0\default.
    public static Process StartAsSystemInSession(string exe, string args, uint sessionId)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x01EF /* duplicate|query|assign|adjust default+sessionid */, out var token))
            throw new Win32Exception();
        try
        {
            if (!DuplicateTokenEx(token, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* impersonation */, 1 /* primary */, out var dup))
                throw new Win32Exception();
            try
            {
                if (!SetTokenInformation(dup, 12 /* TokenSessionId */, ref sessionId, 4)) throw new Win32Exception();
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                if (!CreateProcessAsUser(dup, exe, $"\"{exe}\" {args}", IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /* CREATE_NO_WINDOW */,
                        IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
                    throw new Win32Exception();
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return Process.GetProcessById(pi.dwProcessId);
            }
            finally { CloseHandle(dup); }
        }
        finally { CloseHandle(token); }
    }

    /// Desktop folder of the user logged on at the console; Public Desktop when nobody is (logon screen).
    public static string ConsoleUserDesktop()
    {
        if (WTSQueryUserToken(WTSGetActiveConsoleSessionId(), out var token))
            try
            {
                var desktop = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"); // FOLDERID_Desktop
                if (SHGetKnownFolderPath(ref desktop, 0, token, out var p) == 0)
                    try { return Marshal.PtrToStringUni(p)!; } finally { Marshal.FreeCoTaskMem(p); }
            }
            finally { CloseHandle(token); }
        return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int level, int type, out IntPtr dup);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int cls, ref uint value, int length);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
