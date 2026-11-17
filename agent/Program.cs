using System.Text.Json;
using System.Text.Json.Nodes;

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

        // Public values (apiKey, databaseUrl) are baked into the exe; secrets (TURN token) are never in the exe and
        // come only from an agent.json placed next to it on each PC. Its keys override the baked-in ones.
        var path = Path.Combine(AppContext.BaseDirectory, "agent.json");
        using var embedded = typeof(Program).Assembly.GetManifestResourceStream("agent.json");
        var opt = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        var json = embedded is null ? new JsonObject() : JsonNode.Parse(embedded, documentOptions: opt)!.AsObject();
        if (File.Exists(path))
            foreach (var (k, v) in JsonNode.Parse(File.ReadAllText(path), documentOptions: opt)!.AsObject())
                json[k] = v?.DeepClone();
        var cfg = json.Deserialize<Config>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (string.IsNullOrEmpty(cfg?.ApiKey) || string.IsNullOrEmpty(cfg.DatabaseUrl))
        {
            MessageBox.Show($"설정이 없습니다:\n{path}\n\nagent\\agent.json.example을 agent\\agent.json으로 복사해 값을 채운 뒤 다시 빌드하세요.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        _ = new MainForm(cfg); // starts hidden in the tray
        Application.Run();     // until MainForm.Quit
        GC.KeepAlive(single);
        return 0;
    }
}

public sealed class MainForm : Form
{
    // Colors match the web page (web/style.css).
    static readonly Color Accent = Color.FromArgb(37, 99, 235), Bg = Color.FromArgb(244, 246, 250), Muted = Color.FromArgb(107, 116, 137),
        Ink = Color.FromArgb(26, 34, 51), Danger = Color.FromArgb(211, 58, 58);
    const string UiFont = "Malgun Gothic";

    readonly Label _status = new() { AutoSize = true, ForeColor = Color.White, Font = new Font(UiFont, 9.5f, FontStyle.Bold), Text = "● 연결 중..." };
    readonly Label _id = Value("--- --- ---");
    readonly Label _code = Value("--- ---");
    readonly Label _codeLeft = new() { AutoSize = true, Font = new Font(UiFont, 8.5f), ForeColor = Muted, Margin = new Padding(2, 0, 0, 0) };
    readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64, Enabled = false, Font = new Font(UiFont, 10) };
    readonly Label _remote = new() { AutoSize = true, Font = new Font(UiFont, 10), ForeColor = Muted, Text = "원격 접속 없음", Margin = new Padding(0, 9, 0, 0) };
    readonly Button _disconnect = Btn("원격 해제");
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Consolas", 9) };
    static readonly Icon AppIcon = LoadIcon();
    readonly NotifyIcon _tray = new() { Icon = AppIcon, Visible = true };
    readonly ToolStripMenuItem _trayDisconnect = new("원격 해제") { Enabled = false };
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    readonly Form _banner;
    Label? _bannerText;
    string _who = "";
    readonly CancellationTokenSource _cts = new();
    readonly Agent _agent;
    string _pcId = "", _codeText = "";
    DateTime _codeAt;
    bool _quitting;

    static Label Value(string t) => new() { Text = t, AutoSize = true, Font = new Font("Consolas", 22, FontStyle.Bold), ForeColor = Ink, Margin = new Padding(0, 0, 8, 0) };
    static Button Btn(string t, bool primary = false)
    {
        var b = new Button { Text = t, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, Font = new Font(UiFont, 9.5f), Cursor = Cursors.Hand,
            BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Margin = new Padding(6, 6, 0, 0) };
        b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(214, 219, 228);
        return b;
    }
    // White card: caption on top, then one row of controls.
    static Panel Card(string caption, params Control[] row)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Margin = new Padding(0) };
        flow.Controls.AddRange(row);
        var card = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White, Padding = new Padding(16, 10, 16, 12) };
        card.Controls.Add(flow);
        card.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, Height = 22, Font = new Font(UiFont, 9, FontStyle.Bold), ForeColor = Muted });
        return card;
    }
    static Control Gap(int h = 10) => new Panel { Dock = DockStyle.Top, Height = h };

    public MainForm(Config cfg)
    {
        var title = $"{Program.AppName} v{Agent.Version.Display()}";
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; // laid out in 96-DPI pixels, scaled when shown
        Text = title; ClientSize = new Size(420, 480); MinimumSize = new Size(420, 440); StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon; BackColor = Bg; Font = new Font(UiFont, 9.5f);

        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Accent };
        header.Controls.Add(new Label { Text = "HtUtil Agent", ForeColor = Color.White, Font = new Font(UiFont, 14, FontStyle.Bold), AutoSize = true, Location = new Point(14, 8) });
        header.Controls.Add(new Label { Text = $"v{Agent.Version.Display()} · " + (Agent.ServiceMode ? "서비스 모드 (잠금화면 지원)" : "일반 모드"),
            ForeColor = Color.FromArgb(214, 226, 255), AutoSize = true, Location = new Point(17, 38) });
        header.Controls.Add(_status);
        void placeStatus() => _status.Location = new Point(header.ClientSize.Width - _status.Width - 16, 14);
        header.Resize += (_, _) => placeStatus();
        _status.SizeChanged += (_, _) => placeStatus();

        var copyId = Btn("복사"); copyId.Click += (_, _) => Copy(_pcId, "PC ID");
        var copyCode = Btn("복사"); copyCode.Click += (_, _) => Copy(_codeText, "코드");
        var newCode = Btn("새 코드"); newCode.Click += async (_, _) => { try { await _agent!.RefreshCode(); } catch (Exception e) { Log.Write($"코드 발급 실패: {e.Message}"); } };
        var codeBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        codeBox.Controls.AddRange([_code, _codeLeft]);

        // "이 PC 레벨": users with exactly this level connect without the approval prompt (admins always do).
        for (var i = 1; i <= 9; i++) _level.Items.Add(i);
        _level.Margin = new Padding(0, 4, 10, 0);
        _level.SelectionChangeCommitted += async (_, _) =>
        {
            try { await _agent!.SetMinLevel((int)_level.SelectedItem!); Log.Write($"이 PC 레벨: {_level.SelectedItem}"); }
            catch (Exception e) { Log.Write($"레벨 변경 실패: {e.Message}"); await LoadLevel(); }
        };
        var levelHint = new Label { Text = "같은 레벨 사용자는 승인 없이 접속\n관리자는 항상, 그 외는 승인 창", AutoSize = true, ForeColor = Muted, Font = new Font(UiFont, 8.5f) };

        _disconnect.Visible = false; _disconnect.ForeColor = Danger;
        _disconnect.Click += (_, _) => _agent!.Disconnect();

        var logCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(8), Visible = false };
        logCard.Controls.Add(_log);
        var logToggle = new LinkLabel { Text = "기록 보기 ▾", AutoSize = true, Dock = DockStyle.Top, LinkColor = Muted, ActiveLinkColor = Accent, Padding = new Padding(0, 0, 0, 6) };
        logToggle.Click += (_, _) =>
        {
            logCard.Visible = !logCard.Visible;
            logToggle.Text = logCard.Visible ? "기록 숨기기 ▴" : "기록 보기 ▾";
            if (logCard.Visible && ClientSize.Height < LogicalToDeviceUnits(660)) ClientSize = new Size(ClientSize.Width, LogicalToDeviceUnits(660));
        };

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 12) };
        // Dock=Top stacks in reverse add order: bottom-most first.
        body.Controls.Add(logCard);
        body.Controls.Add(logToggle);
        body.Controls.Add(Gap(8));
        body.Controls.Add(Card("원격 상태", _remote, _disconnect));
        body.Controls.Add(Gap());
        body.Controls.Add(Card("이 PC 레벨", _level, levelHint));
        body.Controls.Add(Gap());
        body.Controls.Add(Card("게스트 코드 · 5분마다 바뀜", codeBox, copyCode, newCode));
        body.Controls.Add(Gap());
        body.Controls.Add(Card("PC ID", _id, copyId));
        Controls.Add(body);
        Controls.Add(header);

        Log.Line += s => UI(() => _log.AppendText(s + Environment.NewLine));
        _agent = new Agent(cfg, this);
        _banner = Banner();

        _clock.Tick += (_, _) => TickCode();
        _clock.Start();

        _tray.Text = title;
        _tray.DoubleClick += (_, _) => ShowWindow();
        _trayDisconnect.Click += (_, _) => _agent.Disconnect();
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Items.AddRange([
            new ToolStripMenuItem("열기", null, (_, _) => ShowWindow()),
            new ToolStripMenuItem("PC ID 복사", null, (_, _) => Copy(_pcId, "PC ID")),
            new ToolStripMenuItem("게스트 코드 복사", null, (_, _) => Copy(_codeText, "코드")),
            _trayDisconnect,
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

    void Copy(string text, string what)
    {
        if (text == "") return;
        try { Clipboard.SetText(text); Notify("복사됨", $"{what}: {text}"); } catch (Exception e) { Log.Write($"복사 실패: {e.Message}"); }
    }

    void TickCode()
    {
        if (_codeText == "") return;
        var left = TimeSpan.FromMinutes(5) - (DateTime.Now - _codeAt);
        _codeLeft.Text = left > TimeSpan.Zero ? $"{left:m\\:ss} 후 새 코드" : "새 코드 받는 중...";
    }

    // Tray-sized (small icon) frame from the embedded multi-size app.ico; DPI-aware via SystemInformation.
    static Icon LoadIcon()
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
        return s is null ? SystemIcons.Application : new Icon(s, SystemInformation.SmallIconSize);
    }

    // Any startup/listen failure (network, DB rules, token) → offline for 30s, then start over. Before, one error left the
    // agent running but permanently offline, which also meant no web update could reach it.
    async Task RunAgent()
    {
        while (!_cts.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token); // ends the old attempt's loops
            try { await _agent.Run(attempt.Token); }
            catch (Exception e) { Log.Write($"오류: {e.Message} — 30초 후 다시 시작"); }
            attempt.Cancel();
            if (_cts.IsCancellationRequested) return;
            SetOnline(false);
            await Task.Delay(30_000, _cts.Token).ContinueWith(_ => { });
        }
    }

    void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); _ = LoadLevel(); }

    // Read on show / after login: the admin page can change it too.
    async Task LoadLevel()
    {
        try
        {
            var v = await _agent.GetMinLevel();
            UI(() => { _level.SelectedItem = v is >= 1 and <= 9 ? v : null; _level.Enabled = v != null; });
        }
        catch { }
    }

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
        if (!_level.Enabled) _ = LoadLevel();
        _pcId = pcId; _codeText = code; _codeAt = DateTime.Now;
        _id.Text = pcId.Length == 9 ? $"{pcId[..3]} {pcId[3..6]} {pcId[6..]}" : pcId;
        _code.Text = $"{code[..3]} {code[3..]}";
        TickCode();
        _tray.Text = $"HtUtil v{Agent.Version.Display()}\nPC {pcId} / 코드 {code}";
    });

    /// Heartbeat result: online = the web page lists this PC as connectable.
    public void SetOnline(bool on) => UI(() => _status.Text = on ? "● 온라인" : "● 오프라인 · 재시도 중");

    public void Notify(string title, string text) => UI(() => _tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info));

    // Small corner window while an update arrives over the web (percent < 0 = no bar change). Closes when we restart.
    Form? _upd; Label? _updText; ProgressBar? _updBar;
    public void UpdateProgress(string text, int percent, int closeAfter = 0) => UI(() =>
    {
        if (_upd == null)
        {
            _updText = new Label { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(UiFont, 10) };
            _updBar = new ProgressBar { Dock = DockStyle.Top, Height = 18 };
            _upd = new Form { Text = "HtUtil Agent 업데이트", FormBorderStyle = FormBorderStyle.FixedToolWindow, TopMost = true, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Width = 340, Height = 100, Padding = new Padding(10, 4, 10, 8), BackColor = Color.White };
            _upd.Controls.Add(_updBar); _upd.Controls.Add(_updText);
            var wa = Screen.PrimaryScreen!.WorkingArea;
            _upd.Location = new Point(wa.Right - _upd.Width - 12, wa.Bottom - _upd.Height - 12);
            _upd.FormClosed += (_, _) => _upd = null;
            _upd.Show();
        }
        _updText!.Text = text;
        if (percent >= 0) _updBar!.Value = Math.Clamp(percent, 0, 100);
        if (closeAfter > 0) { var f = _upd; _ = Task.Delay(closeAfter).ContinueWith(_ => UI(() => f.Close())); }
    });

    public void SetRemoteActive(bool on, string who) => UI(() =>
    {
        _banner.Visible = on; _trayDisconnect.Enabled = on; _disconnect.Visible = on;
        _who = who;
        _remote.Text = on ? $"● 원격 제어 중 · {who}" : $"원격 접속 없음 · 마지막: {who} ({DateTime.Now:HH:mm})";
        _remote.ForeColor = on ? Danger : Muted;
        _tray.ShowBalloonTip(4000, on ? "원격 접속 시작" : "원격 접속 종료", on ? $"{who} 님이 이 PC를 원격 제어합니다." : $"{who} 님의 원격 접속이 끝났습니다.", ToolTipIcon.Info);
    });

    /// Viewer asked for a file from this PC: a file dialog on this screen, which the viewer operates remotely.
    public string? PickFile()
    {
        string? path = null;
        Invoke(() =>
        {
            using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 };
            owner.Show();
            using var d = new OpenFileDialog { Title = "원격으로 가져갈 파일 선택 (HtUtil)", CheckFileExists = true };
            // service mode = SYSTEM: start in the console user's folders, not the system profile's
            var desk = Agent.ServiceMode ? Native.ConsoleUserDesktop() : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(desk)) d.InitialDirectory = desk;
            if (d.ShowDialog(owner) == DialogResult.OK) path = d.FileName;
        });
        return path;
    }

    /// Admin session options (Privacy.cs). Returns false if blanking isn't supported here.
    public bool Privacy(bool? blank = null, bool? lockInput = null)
    {
        var ok = true;
        Invoke(() =>
        {
            if (blank is { } b) ok = HtUtil.Agent.Privacy.Blank(b);
            if (lockInput is { } on && on != HtUtil.Agent.Privacy.Locked)
            {
                // visible on this PC: banner text + main window + tray notice (the blank screen shows it too)
                _bannerText!.Text = on ? "● 원격 제어 중 · 입력 잠금" : "● 원격 제어 중";
                _banner.Width = on ? 360 : 280;
                _banner.Left = Screen.PrimaryScreen!.Bounds.Width / 2 - _banner.Width / 2;
                _remote.Text = $"● 원격 제어 중 · {_who}" + (on ? " · 입력 잠금" : "");
                _tray.ShowBalloonTip(5000, on ? "입력 잠금" : "입력 잠금 해제",
                    on ? "관리자가 원격 작업 중이라 이 PC의 키보드·마우스가 잠겼습니다.\n(Ctrl+Alt+Del은 항상 사용 가능)" : "이 PC의 키보드·마우스를 다시 쓸 수 있습니다.", ToolTipIcon.Info);
            }
            if (lockInput is { } l) HtUtil.Agent.Privacy.Lock(l);
        });
        return ok;
    }

    /// Blocks the calling (background) thread until the local user answers. null = no answer within `seconds`.
    public bool? Ask(string msg, int seconds)
    {
        bool? result = null;
        Invoke(() =>
        {
            using var f = new Form { TopMost = true, Text = "원격 제어 요청", StartPosition = FormStartPosition.CenterScreen, Icon = AppIcon,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, BackColor = Color.White,
                AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(380, 196), Font = new Font(UiFont, 10) };
            var bar = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Accent };
            var text = new Label { Text = msg, Location = new Point(20, 24), Size = new Size(340, 64), Font = new Font(UiFont, 10.5f), ForeColor = Ink };
            var left = new Label { Location = new Point(20, 94), AutoSize = true, ForeColor = Muted };
            var progress = new ProgressBar { Location = new Point(20, 120), Size = new Size(340, 6), Maximum = seconds * 2, Value = seconds * 2 };
            var yes = Btn("허용", primary: true); yes.DialogResult = DialogResult.Yes; yes.AutoSize = false; yes.Size = new Size(96, 36); yes.Location = new Point(158, 142);
            var no = Btn("거부"); no.DialogResult = DialogResult.No; no.AutoSize = false; no.Size = new Size(96, 36); no.Location = new Point(264, 142);
            f.Controls.AddRange([bar, text, left, progress, yes, no]);
            f.AcceptButton = no; f.CancelButton = no;
            var end = DateTime.Now.AddSeconds(seconds);
            var t = new System.Windows.Forms.Timer { Interval = 500 };
            void tick()
            {
                var ms = (end - DateTime.Now).TotalMilliseconds;
                if (ms <= 0) { f.DialogResult = DialogResult.Abort; return; }
                left.Text = $"{Math.Ceiling(ms / 1000)}초 후 자동 취소";
                progress.Value = Math.Clamp((int)(ms / 500), 0, progress.Maximum);
            }
            t.Tick += (_, _) => tick();
            tick(); t.Start();
            System.Media.SystemSounds.Exclamation.Play();
            var r = f.ShowDialog(); // no owner: the main window is usually hidden in the tray
            t.Dispose();
            result = r == DialogResult.Yes ? true : r == DialogResult.Abort ? null : false;
        });
        return result;
    }

    Form Banner()
    {
        var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, BackColor = Danger,
            StartPosition = FormStartPosition.Manual, Width = 280, Height = 34,
        };
        f.Location = new Point(Screen.PrimaryScreen!.Bounds.Width / 2 - 140, 0);
        var stop = new Button { Text = "해제", Dock = DockStyle.Right, Width = 64, BackColor = Color.White, ForeColor = Danger, FlatStyle = FlatStyle.Flat,
            Font = new Font(UiFont, 9.5f, FontStyle.Bold), Cursor = Cursors.Hand };
        stop.FlatAppearance.BorderSize = 0;
        stop.Click += (_, _) => _agent.Disconnect();
        f.Controls.Add(_bannerText = new Label { Text = "● 원격 제어 중", ForeColor = Color.White, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, Font = new Font(UiFont, 11, FontStyle.Bold) });
        f.Controls.Add(stop);
        return f;
    }
}
