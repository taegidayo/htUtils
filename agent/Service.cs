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
    public const string OldName = "HtUtilAgent"; // before the rename to HtUtil (removed on install)
    /// Process names of this agent, current and pre-rename (both must be stopped to free the single-instance mutex).
    public static readonly string[] ProcessNames = ["HtUtil.Agent", "HtUtil.Agent"];
    readonly string _exe = Environment.ProcessPath!;
    readonly object _lock = new();
    System.Threading.Timer? _timer;
    Process? _session;
    uint _sessionId = uint.MaxValue;
    bool _cleaned;

    public AgentService() { ServiceName = Name; CanStop = true; }

    protected override void OnStart(string[] args) => _timer = new System.Threading.Timer(_ => Tick(), null, 3000, 2000);

    protected override void OnStop() { _timer?.Dispose(); try { _session?.Kill(); } catch { } }

    void Tick()
    {
        lock (_lock)
        {
            try
            {
                // The web update swapped the exe on disk: exit so the SCM recovery action restarts us from the new file.
                var fv = FileVersionInfo.GetVersionInfo(_exe);
                if (new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart) > Agent.Version) Environment.Exit(1);

                if (!_cleaned) // once, 3s after start (the installer has exited by then)
                {
                    _cleaned = true;
                    try { Installer.SecureDataDir(); } catch { } // identity must be readable by the SYSTEM session agent
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
    static readonly string OldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HtUtil"); // pre-rename install
    // DataDir keeps its pre-rename name on purpose: it holds this PC's identity (PC ID).
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
            foreach (var name in new[] { AgentService.Name, AgentService.OldName })
                using (var sc = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == name))
                    if (sc is { Status: not ServiceControllerStatus.Stopped }) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
            // pre-rename install (HtUtilAgent service, Program Files\HtUtil): remove, the new one replaces it
            if (Run("sc", $"query {AgentService.OldName}") == 0) Run("sc", $"delete {AgentService.OldName}");
            KillOtherAgents();
            try { if (Directory.Exists(OldDir) && !Environment.ProcessPath!.StartsWith(OldDir, StringComparison.OrdinalIgnoreCase)) Directory.Delete(OldDir, true); } catch { }

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
            SecureDataDir();

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
            foreach (var name in new[] { AgentService.Name, AgentService.OldName }) Run("sc", $"stop {name}");
            Thread.Sleep(2000);
            foreach (var name in new[] { AgentService.Name, AgentService.OldName }) Run("sc", $"delete {name}");
            KillOtherAgents();
            Thread.Sleep(1000);
            foreach (var dir in new[] { Dir, OldDir })
                if (Directory.Exists(dir) && !Environment.ProcessPath!.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) Directory.Delete(dir, true);
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

    /// Other agent copies (session agents, user-mode agents, pre-rename HtUtil.Agent) hold the single-instance mutex.
    public static void KillOtherAgents()
    {
        foreach (var name in AgentService.ProcessNames)
            foreach (var p in Process.GetProcessesByName(name))
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

    public static void Sync()
    {
        var h = OpenInputDesktop(0, false, 0x10000000 /* GENERIC_ALL */);
        if (h == IntPtr.Zero) return; // user mode cannot open the secure desktop: stay where we are
        var name = Name(h);
        if (name == _current || !SetThreadDesktop(h)) { CloseDesktop(h); return; }
        _current = name; // ponytail: the thread's previous desktop handle is not closed (one handle per lock/unlock per thread)
    }

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
