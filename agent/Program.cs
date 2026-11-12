using System.Text.Json;

namespace HtUtil.Agent;

static class Program
{
    public const string AppName = "HtUtil Agent";

    /// (no args)     double-click: install/hand over to the Windows service (UAC); declined → user mode
    /// --service     run by the SCM: keeps a "--session" copy running in the console session (Service.cs)
    /// --session     SYSTEM agent in the console session (started by the service; lock screen capable)
    /// --user        plain user-mode agent (no service)
    /// --install / --uninstall   elevated (un)installation
    [STAThread]
    static int Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "";
        if (mode == "--service") { System.ServiceProcess.ServiceBase.Run(new AgentService()); return 0; }
        ApplicationConfiguration.Initialize();
        if (mode == "--install") return Installer.Install();
        if (mode == "--uninstall") return Installer.Uninstall();
#if !DEBUG // debug runs from the IDE stay in user mode
        if (mode == "") try { if (Installer.HandOffToService()) return 0; } catch (Exception e) { Log.Write($"service: {e.Message}"); }
#endif
        Agent.ServiceMode = mode == "--session";

        // One agent per session. Waits a little so a freshly updated exe can start while the old one exits.
        Mutex single;
        try { single = new Mutex(false, @"Local\HtUtilAgent"); }
        catch (UnauthorizedAccessException) // created by the SYSTEM session agent
        {
            MessageBox.Show("HtUtil Agent가 이미 서비스로 실행 중입니다.", AppName);
            return 0;
        }
        try { if (!single.WaitOne(TimeSpan.FromSeconds(15))) return 0; }
        catch (AbandonedMutexException) { } // previous instance died without releasing: we own it now

        // agent.json next to the exe wins; otherwise the copy baked in at build time (single-exe download).
        var path = Path.Combine(AppContext.BaseDirectory, "agent.json");
        using var embedded = typeof(Program).Assembly.GetManifestResourceStream("agent.json");
        var json = File.Exists(path) ? File.ReadAllText(path) : embedded is null ? null : new StreamReader(embedded).ReadToEnd();
        if (json is null)
        {
            MessageBox.Show($"설정이 없습니다:\n{path}\n\nagent\\agent.json.example을 agent\\agent.json으로 복사해 값을 채운 뒤 다시 빌드하세요.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        var cfg = JsonSerializer.Deserialize<Config>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip })!;
        _ = new MainForm(cfg); // starts hidden in the tray
        Application.Run();     // until MainForm.Quit
        GC.KeepAlive(single);
        return 0;
    }
}

public sealed class MainForm : Form
{
    readonly Label _id = new() { Dock = DockStyle.Top, Height = 40, Font = new Font("Segoe UI", 14) };
    readonly Label _code = new() { Dock = DockStyle.Top, Height = 60, Font = new Font("Consolas", 28, FontStyle.Bold) };
    readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    static readonly Icon AppIcon = LoadIcon();
    readonly NotifyIcon _tray = new() { Icon = AppIcon, Visible = true };
    readonly ToolStripMenuItem _trayDisconnect = new("원격 해제") { Enabled = false };
    readonly Form _banner;
    readonly CancellationTokenSource _cts = new();
    readonly Agent _agent;
    bool _quitting;

    public MainForm(Config cfg)
    {
        var title = $"{Program.AppName} v{Agent.Version.Display()}";
        Text = title; Width = 420; Height = 360; StartPosition = FormStartPosition.CenterScreen; Icon = AppIcon;
        Controls.Add(_log); Controls.Add(_code); Controls.Add(_id);
        _id.Text = "로그인 중...";
        Log.Line += s => UI(() => _log.AppendText(s + Environment.NewLine));
        _agent = new Agent(cfg, this);
        _banner = Banner();

        _tray.Text = title;
        _tray.DoubleClick += (_, _) => ShowWindow();
        _trayDisconnect.Click += (_, _) => _agent.Disconnect();
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Items.AddRange([
            new ToolStripMenuItem("열기", null, (_, _) => ShowWindow()), _trayDisconnect,
            new ToolStripSeparator(), new ToolStripMenuItem("종료", null, (_, _) => ConfirmQuit()),
        ]);

        // X button hides to the tray; only "종료" / Windows shutdown really exits.
        FormClosing += (_, e) => { if (!_quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };

        _ = Handle; // window stays hidden but needs a handle for BeginInvoke / dialogs
        _ = RunAgent();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
        _tray.ShowBalloonTip(5000, $"{Program.AppName} 실행 중",
            $"v{Agent.Version.Display()} — 작업 표시줄 트레이에서 실행 중입니다.\n아이콘을 더블클릭하면 창이 열립니다.", ToolTipIcon.Info);
    }

    // Tray-sized (small icon) frame from the embedded multi-size app.ico; DPI-aware via SystemInformation.
    static Icon LoadIcon()
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
        return s is null ? SystemIcons.Application : new Icon(s, SystemInformation.SmallIconSize);
    }

    async Task RunAgent()
    {
        try { await _agent.Run(_cts.Token); }
        catch (Exception e) { Log.Write($"fatal: {e.Message}"); }
    }

    void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    void ConfirmQuit()
    {
        var msg = Agent.ServiceMode
            ? "HtUtil Agent를 종료할까요?\n\n다시 실행하거나 PC를 재부팅할 때까지 이 PC에 원격 접속할 수 없습니다."
            : "HtUtil Agent를 종료할까요?\n\n다시 실행할 때까지 이 PC에 원격 접속할 수 없습니다.";
        if (MessageBox.Show(msg, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Quit(stopService: true);
    }

    /// Exit the app. goOffline=false when an updated instance is taking over.
    /// stopService: the user chose "종료" in service mode — otherwise the service would start us again in 2s.
    /// Stopping it (we are SYSTEM) also ends this process (OnStop kills the session agent); it starts again on reboot
    /// or when the exe is run again.
    public void Quit(bool goOffline = true, bool stopService = false) => UI(async () =>
    {
        if (_quitting) return;
        _quitting = true;
        _cts.Cancel();
        if (goOffline) try { await _agent.GoOffline(); } catch { }
        _tray.Visible = false;
        if (stopService && Agent.ServiceMode)
            try { using var sc = new System.ServiceProcess.ServiceController(AgentService.Name); sc.Stop(); } catch (Exception e) { Log.Write($"서비스 중지 실패: {e.Message}"); }
        Application.Exit();
    });

    void UI(Action a) { if (IsHandleCreated) BeginInvoke(a); }

    public void ShowCode(string pcId, string code) => UI(() =>
    {
        _id.Text = $"PC ID: {pcId}";
        _code.Text = $"코드: {code[..3]} {code[3..]}";
        _tray.Text = $"HtUtil v{Agent.Version.Display()}\nPC {pcId} / 코드 {code}";
    });

    public void Notify(string title, string text) => UI(() => _tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info));

    public void SetRemoteActive(bool on) => UI(() => { _banner.Visible = on; _trayDisconnect.Enabled = on; });

    /// Blocks the calling (background) thread until the local user answers; auto-denies after 30s.
    public bool Ask(string msg)
    {
        var result = false;
        Invoke(() =>
        {
            using var f = new Form { TopMost = true, Text = "원격 제어 요청", Width = 360, Height = 180, StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
            var yes = new Button { Text = "허용", DialogResult = DialogResult.Yes, Left = 150, Top = 90, Width = 80 };
            var no = new Button { Text = "거부", DialogResult = DialogResult.No, Left = 240, Top = 90, Width = 80 };
            f.Controls.AddRange([new Label { Text = msg, Left = 15, Top = 15, Width = 320, Height = 60 }, yes, no]);
            f.AcceptButton = no; f.CancelButton = no;
            var t = new System.Windows.Forms.Timer { Interval = 30_000 };
            t.Tick += (_, _) => f.DialogResult = DialogResult.No;
            t.Start();
            result = f.ShowDialog() == DialogResult.Yes; // no owner: the main window is usually hidden in the tray
            t.Dispose();
        });
        return result;
    }

    Form Banner()
    {
        var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, BackColor = Color.Red,
            StartPosition = FormStartPosition.Manual, Width = 260, Height = 32,
        };
        f.Location = new Point(Screen.PrimaryScreen!.Bounds.Width / 2 - 130, 0);
        var stop = new Button { Text = "해제", Dock = DockStyle.Right, Width = 60, BackColor = Color.White, FlatStyle = FlatStyle.Flat };
        stop.Click += (_, _) => _agent.Disconnect();
        f.Controls.Add(new Label { Text = "● 원격 제어 중", ForeColor = Color.White, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12, FontStyle.Bold) });
        f.Controls.Add(stop);
        return f;
    }
}
