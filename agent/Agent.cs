using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace HtUtil.Agent;

// agent.json holds only values shared by every PC; per-PC identity is created on first run.
public record Config(string ApiKey, string DatabaseUrl);

public sealed class Agent(Config cfg, MainForm ui)
{
    /// Running as the service's SYSTEM session agent (lock screen capable) instead of a plain user process.
    public static bool ServiceMode;

    // Outside the program folder so copying the folder to another PC never clones the identity.
    // Service mode: ProgramData, readable only by SYSTEM/Admins (set by the installer).
    // ponytail: plaintext refresh token (only grants this PC's agent rights); DPAPI if that matters.
    public static readonly string UserIdPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HtUtil", "identity.json");
    static string IdPath => ServiceMode ? Path.Combine(Installer.DataDir, "identity.json") : UserIdPath;
    // Cloudflare TURN key pushed by a level-9 admin over the update DataChannel (never in the exe / DB).
    public static readonly string UserTurnPath = Path.Combine(Path.GetDirectoryName(UserIdPath)!, "turn.json");
    static string TurnPath => ServiceMode ? Path.Combine(Installer.DataDir, "turn.json") : UserTurnPath;

    readonly Firebase _fb = new(cfg.ApiKey, cfg.DatabaseUrl);
    readonly HashSet<string> _seen = [];
    readonly SemaphoreSlim _busy = new(1, 1); // ponytail: one remote session at a time
    string Pc = "";

    public async Task Run(CancellationToken ct)
    {
        await SignInOrRegister();
        Log.Write($"signed in as {_fb.Uid}");
        Pc = await Register();
        await ReportInfo();
        Log.Write($"pc={Pc}, version={Version.Display()}");
        await _fb.Patch($"pcs/{Pc}", new { online = true, lastSeen = Firebase.ServerTime, version = Version.Display() });
        await _fb.Delete($"requests/{Pc}");
        await _fb.Delete($"sessions/{Pc}"); // leftovers, e.g. from the process that just updated us
        // a session that ended with a crash / reboot. Optional: DB rules older than this agent refuse it (401) —
        // that once stopped the whole agent at startup (v26.11.13.1 before the rules were deployed).
        try { await _fb.Delete($"pcs/{Pc}/remote"); } catch (Exception e) { Log.Write($"원격 상태 지우기 실패 (DB 규칙 배포 필요?): {e.Message}"); }
        _ = Task.Delay(10_000).ContinueWith(_ =>
        {
            if (ServiceMode) try { Installer.KillStaleService(); } catch (Exception e) { Log.Write($"service check: {e.Message}"); }
            // leftovers of earlier updates ("exe.old", or "exe.old.<n>" when an old one was still running)
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Environment.ProcessPath)!, Path.GetFileName(Environment.ProcessPath) + ".old*"))
                try { File.Delete(f); } catch { }
        });

        ui.SetOnline(true);
        _ = Loop(TimeSpan.FromSeconds(30), async () =>
        {
            try { await _fb.Patch($"pcs/{Pc}", new { online = true, lastSeen = Firebase.ServerTime }); ui.SetOnline(true); }
            catch { ui.SetOnline(false); throw; }
        }, ct);
        _ = Loop(TimeSpan.FromSeconds(5), async () => { if (DateTime.UtcNow - _codeAt >= TimeSpan.FromMinutes(5)) await NewCode(); }, ct);
        await _fb.Listen($"requests/{Pc}", (_, _) => _ = OnRequestsChanged(), ct);
    }

    // This PC's level (pcs/{id}/minLevel), also editable from the agent window. Same level = no approval prompt.
    public async Task<int?> GetMinLevel() => Pc == "" ? null : (int?)await _fb.Get($"pcs/{Pc}/minLevel");
    public Task SetMinLevel(int v) => _fb.Put($"pcs/{Pc}/minLevel", v);
    public Task GoOffline() => Pc == "" ? Task.CompletedTask : _fb.Patch($"pcs/{Pc}", new { online = false, lastSeen = Firebase.ServerTime });

    async Task SignInOrRegister()
    {
        if (File.Exists(IdPath))
        {
            await _fb.SignIn((string)JsonNode.Parse(File.ReadAllText(IdPath))!["refreshToken"]!);
            return;
        }
        await _fb.SignUpAnonymously();
        Directory.CreateDirectory(Path.GetDirectoryName(IdPath)!);
        File.WriteAllText(IdPath, JsonSerializer.Serialize(new { uid = _fb.Uid, refreshToken = _fb.RefreshToken }));
    }

    // Refreshed every start (IP, user, OS) for the admin page.
    Task ReportInfo() => _fb.Put($"agentInfo/{_fb.Uid}", new
    {
        machineName = Environment.MachineName, ip = LocalIp(), user = Environment.UserName,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, updatedAt = Firebase.ServerTime,
    });

    // LAN IPv4 of the default route (a UDP connect sends no packet).
    static string LocalIp()
    {
        try
        {
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            s.Connect("8.8.8.8", 53);
            return ((System.Net.IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return ""; }
    }

    // First run registers the PC by itself (no approval). pcId = random 9 digits (what guests type);
    // name = admin's IP→name mapping, else the computer name. Admins rename / delete it in the web page.
    async Task<string> Register()
    {
        if ((string?)await _fb.Get($"agents/{_fb.Uid}") is { } pc) return pc;
        var ip = LocalIp();
        var name = ip == "" ? null : (string?)await _fb.Get($"ipNames/{ip.Replace('.', '_')}");
        name ??= Environment.MachineName;
        var id = Random.Shared.Next(100_000_000, 1_000_000_000).ToString(); // rules refuse an existing id
        await _fb.Patch("", new JsonObject
        {
            [$"pcs/{id}"] = new JsonObject { ["name"] = name, ["agentUid"] = _fb.Uid, ["minLevel"] = 2, ["allowGuest"] = false, ["online"] = false },
            [$"agents/{_fb.Uid}"] = id,
        });
        Log.Write($"PC 등록됨: {id} ({name})");
        return id;
    }

    static volatile bool _netDown;
    static async Task Loop(TimeSpan every, Func<Task> f, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await f(); if (_netDown) { _netDown = false; Log.Write("네트워크 다시 연결됨"); } }
            catch (HttpRequestException e) // DNS/network down (sleep, Wi-Fi drop, port exhaustion): log once, not every 5s
            {
                if (!_netDown) { _netDown = true; Log.Write($"네트워크 끊김 — 자동으로 다시 시도합니다 ({e.Message})"); }
            }
            catch (Exception e) { Log.Write(e.Message); }
            await Task.Delay(every, ct).ContinueWith(_ => { });
        }
    }

    public Task RefreshCode() => Pc == "" ? Task.CompletedTask : NewCode();

    DateTime _codeAt; // "새 코드" button resets the 5-minute cycle
    async Task NewCode()
    {
        _codeAt = DateTime.UtcNow;
        var code = Random.Shared.Next(0, 1_000_000).ToString("D6");
        // codeIssuedAt uses server time; rules accept the code for 5 min after it.
        await _fb.Put($"pcSecrets/{Pc}", new { code, codeIssuedAt = Firebase.ServerTime });
        ui.ShowCode(Pc, code);
    }

    async Task OnRequestsChanged()
    {
        if (await _fb.Get($"requests/{Pc}") is not JsonObject reqs) return;
        foreach (var (id, r) in reqs)
            if ((string?)r?["status"] == "pending" && _seen.Add(id))
                _ = (string?)r!["mode"] == "kick" ? Kick(id, (string)r["uid"]!) : Handle(id, (string)r["uid"]!, (string)r["mode"]!);
    }

    // Admin web "강제 해제": ends the current session (not queued behind it like other requests — _busy is held by it).
    // The web blocks the account itself (users/{uid}/blocked); here we only re-check the requester and end it.
    bool _kicked;
    async Task Kick(string reqId, string uid)
    {
        var reqPath = $"requests/{Pc}/{reqId}";
        try
        {
            var ok = (int?)(await _fb.Get($"users/{uid}"))?["level"] == 9;
            await _fb.Patch(reqPath, new { status = ok ? "approved" : "denied" });
            if (ok && _endSession is { } end)
            {
                Log.Write("관리자가 원격을 강제 해제");
                _kicked = true; end();
                await NewCode(); // a kicked guest can't come straight back with the same code
            }
        }
        catch (Exception e) { Log.Write($"강제 해제: {e.Message}"); }
        await Task.Delay(3000);
        try { await _fb.Delete(reqPath); } catch { }
    }

    async Task Handle(string reqId, string uid, string mode)
    {
        var reqPath = $"requests/{Pc}/{reqId}";
        try
        {
            if (!await _busy.WaitAsync(0)) { await _fb.Patch(reqPath, new { status = "denied", reason = "busy" }); return; }
            try
            {
                var ok = await Allowed(uid, mode);
                if (ok != true) { await _fb.Patch(reqPath, new { status = ok == null ? "timeout" : "denied" }); return; }
                var logId = Guid.NewGuid().ToString("N");
                await _fb.Put($"logs/{logId}", new { uid, mode, pcId = Pc, startedAt = Firebase.ServerTime });
                try { await (mode == "update" ? RunUpdate(reqId) : RunSession(reqId)); }
                finally { await _fb.Patch($"logs/{logId}", new { endedAt = Firebase.ServerTime }); }
            }
            finally { _busy.Release(); }
        }
        catch (Exception e) { Log.Write($"session {reqId}: {e.Message}"); }
        await Task.Delay(3000); // let the viewer read the final status before cleanup
        try { await _fb.Delete($"sessions/{Pc}/{reqId}"); await _fb.Delete(reqPath); } catch { }
    }

    // Second check on top of DB rules: re-read level / minLevel, ask the local user when required.
    // null = nobody answered within settings/approvalTimeout seconds (admin page, default 30).
    string _who = "", _uid = ""; bool _admin; // of the request being handled (one session at a time, see _busy)
    async Task<bool?> Allowed(string uid, string mode)
    {
        _who = "게스트"; _uid = ""; _admin = false;
        var wait = Math.Clamp((int?)await _fb.Get("settings/approvalTimeout") ?? 30, 10, 600);
        if (mode == "guest") return ui.Ask($"게스트가 이 PC의 원격 제어를 요청했습니다.\n허용하시겠습니까?", wait);
        var user = await _fb.Get($"users/{uid}");
        var level = (int?)user?["level"] ?? 0;
        _who = (string?)user?["name"] ?? uid; _uid = uid; _admin = level == 9 && mode == "account";
        if (mode == "account" && (bool?)user?["blocked"] == true) { Log.Write($"{_who}: 관리자가 원격을 차단한 계정, 거부"); return false; }
        if (mode == "update") return level == 9;
        if (mode != "account") return false;
        var minLevel = (int?)await _fb.Get($"pcs/{Pc}/minLevel") ?? 1;
        if (level < 1) return false;
        // No prompt for admins (9) and for a level equal to the PC's minLevel; any other level asks the local user.
        if (level == 9 || level == minLevel) return true;
        return ui.Ask($"{user?["name"]} 님(레벨 {level})이 원격 제어를 요청했습니다.\n허용하시겠습니까?", wait);
    }

    /// Agent = offerer: publish offer + ICE servers, mark the request approved, wait for the browser's answer.
    async Task Offer(RTCPeerConnection pc, string reqId, JsonArray iceServers)
    {
        var sPath = $"sessions/{Pc}/{reqId}";
        pc.onicecandidate += c =>
        {
            Log.Write($"ice local: {c.type} {c.address}:{c.port}");
            _ = _fb.Post($"{sPath}/callerCandidates", JsonNode.Parse(c.toJSON())!);
        };
        pc.oniceconnectionstatechange += s => Log.Write($"ice: {s}");
        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);
        await _fb.Put(sPath, new { iceServers, offer = new { type = "offer", offer.sdp } });
        await _fb.Patch($"requests/{Pc}/{reqId}", new { status = "approved" });

        // The browser's answer carries its ICE candidates in the SDP.
        var answerTcs = new TaskCompletionSource<string>();
        using var answerCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _ = _fb.Listen($"{sPath}/answer/sdp", (p, d) => { if (p == "/" && d != null) answerTcs.TrySetResult((string)d!); }, answerCts.Token);
        var sdp = await answerTcs.Task.WaitAsync(answerCts.Token);
        answerCts.Cancel();
        _answerSdp = sdp; // for the country check after connecting
        foreach (var line in sdp.Split('\n').Where(l => l.StartsWith("a=candidate:")))
            Log.Write($"ice remote: {string.Join(' ', line.Trim().Split(' ').Skip(4).Take(4))}"); // address port typ X
        var r = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        if (r != SetDescriptionResultEnum.OK) throw new Exception($"answer 거부: {r}");
    }

    /// Completes when the peer connection reaches `connected`; throws on failure or after `timeout`.
    static Task WaitConnected(RTCPeerConnection pc, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pc.onconnectionstatechange += s =>
        {
            if (s == RTCPeerConnectionState.connected) tcs.TrySetResult();
            if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) tcs.TrySetException(new Exception($"연결 실패 ({s})"));
        };
        if (pc.connectionState == RTCPeerConnectionState.connected) tcs.TrySetResult();
        return tcs.Task.WaitAsync(timeout).ContinueWith(t => t.IsFaulted && t.Exception!.InnerException is TimeoutException
            ? throw new Exception($"{timeout.TotalSeconds:0}초 안에 P2P 연결이 안 됨 (방화벽/NAT → TURN 필요)") : t).Unwrap();
    }

    async Task RunSession(string reqId)
    {
        var who = _who; var uid = _uid; var admin = _admin;
        _kicked = false;
        var iceServers = await IceServers();
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = ToSipIce(iceServers) });
        // SIPSorceryMedia.Encoders 10.0.4 (latest) is built against Abstractions 8.0.12, but SIPSorcery 10 loads
        // Abstractions 10, whose VideoCodecsEnum is shifted by one (Unknown=0 added): the encoder's "VP8" (7) reads
        // as H263 → offer says H263 → Chrome rejects video → setRemoteDescription = VideoIncompatible.
        // So: advertise VP8 with SIPSorcery 10's enum here; ScreenStream gives the encoder its own value.
        pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.VP8, 96), MediaStreamStatusEnum.SendOnly));
        pc.addTrack(new MediaStreamTrack(AudioStream.Opus, MediaStreamStatusEnum.SendOnly)); // this PC's sound
        var dc = await pc.createDataChannel("input");
        var fileDc = await pc.createDataChannel("file"); // separate channel: big files don't delay mouse/keys
        FileDrop.Attach(fileDc, ui);
        var stream = new ScreenStream(pc, dc);
        var lastMsg = DateTime.UtcNow; // viewer sends {t:"hb"} every 2s; silence = browser gone (tab closed, crash)
        string? lastClip = null;
        void Send(object o) { try { if (dc.readyState == RTCDataChannelState.open) dc.send(JsonSerializer.Serialize(o)); } catch { } }
        void Info(string m) => Send(new { t = "msg", m });
        dc.onmessage += (_, _, data) =>
        {
            lastMsg = DateTime.UtcNow;
            try
            {
                var m = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(data))!;
                switch ((string?)m["t"])
                {
                    case "mon": stream.Select((int)m["i"]!); break;
                    case "hb": break;
                    case "clip": // viewer's clipboard → ours (remembered so the poll below doesn't echo it back)
                        lastClip = (string?)m["s"];
                        if (lastClip != null) { Clip.Set(lastClip); Log.Write($"클립보드 받음 ({lastClip.Length}자)"); }
                        break;
                    case "cad": Info(Sas.Request() ? "Ctrl+Alt+Del 보냄" : "Ctrl+Alt+Del은 서비스 모드(관리자 설치)에서만 됩니다"); break;
                    case "getfile": // file picker on this screen; the viewer operates it, then the file goes back over "file"
                        _ = Task.Run(async () =>
                        {
                            var path = ui.PickFile();
                            if (path == null) return;
                            try { Info($"보내는 중: {Path.GetFileName(path)}"); await FileDrop.Send(fileDc, path); }
                            catch (Exception e) { Info($"파일 보내기 실패: {e.Message}"); }
                        });
                        break;
                    // admin-only from here (level 9 account session)
                    case "blank" when admin: Info(ui.Privacy(blank: (bool)m["on"]!) ? ((bool)m["on"]! ? "화면 가림: 이 PC 화면이 검게 보입니다" : "화면 가림 해제") : "화면 가리기 실패: Windows 10 2004 이상 필요"); break;
                    case "lock" when admin: ui.Privacy(lockInput: (bool)m["on"]!); Info((bool)m["on"]! ? "입력 잠금: 이 PC의 키보드·마우스가 막혔습니다 (Ctrl+Alt+Del은 항상 가능)" : "입력 잠금 해제"); break;
                    case "reboot" when admin:
                        Log.Write($"원격 재부팅 ({who})");
                        Info("재부팅합니다");
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 3 /f") { CreateNoWindow = true });
                        break;
                    default: Input.Handle(m); break;
                }
            }
            catch { }
        };
        dc.onopen += () =>
        {
            Log.Write($"session {reqId}: 입력 채널 열림");
            Send(new { t = "caps", admin, service = ServiceMode });
            stream.SendMonitors();
        };

        var done = new TaskCompletionSource();
        var cts = new CancellationTokenSource();
        AudioStream? audio = null;
        _endSession = () => done.TrySetResult();
        dc.onclose += () => done.TrySetResult();
        pc.onconnectionstatechange += s =>
        {
            Log.Write($"session {reqId}: {s}");
            if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected) done.TrySetResult();
        };
        try
        {
            await Offer(pc, reqId, iceServers);
            await WaitConnected(pc, TimeSpan.FromSeconds(20)); // "원격 중" only once media can actually flow
            if (IsSelf(pc))
            {
                Log.Write($"session {reqId}: 같은 PC에서 접속, 거부");
                Send(new { t = "bye", m = "지금 사용 중인 PC 자신에게는 원격 접속할 수 없습니다." });
                await Task.Delay(1000);
                return;
            }
            if (await ForeignBlock(pc) is { } why)
            {
                Log.Write($"session {reqId}: {why} ({who})");
                Send(new { t = "bye", m = why });
                await Task.Delay(1000);
                return;
            }
            lastMsg = DateTime.UtcNow; // heartbeat window starts now
            _ = stream.Run(cts.Token);
            try { audio = new AudioStream(pc); } catch (Exception e) { Log.Write($"audio: 사용 안 함 ({e.Message})"); }
            ui.SetRemoteActive(true, who);
            try { await _fb.Put($"pcs/{Pc}/remote", uid == "" ? new { who, since = Firebase.ServerTime } : (object)new { who, uid, since = Firebase.ServerTime }); } catch (Exception e) { Log.Write($"원격 상태 기록 실패: {e.Message}"); } // shown on the web PC list
            var clipSeq = Clip.Seq;
            // Ends on: viewer hang-up / tab close (DataChannel closes), no heartbeat for 10s, connection loss, local "해제".
            while (!done.Task.IsCompleted)
            {
                await Task.WhenAny(done.Task, Task.Delay(700));
                if (DateTime.UtcNow - lastMsg > TimeSpan.FromSeconds(10)) { Log.Write($"session {reqId}: 뷰어 응답 없음 (10초), 종료"); break; }
                if (Clip.Seq != clipSeq) // our clipboard changed → viewer
                {
                    clipSeq = Clip.Seq;
                    var text = Clip.Get();
                    if (text != null && text != lastClip && text.Length <= 100_000) { lastClip = text; Send(new { t = "clip", s = text }); Log.Write($"클립보드 보냄 ({text.Length}자)"); }
                }
            }
        }
        finally
        {
            _endSession = null;
            if (_kicked) { Send(new { t = "bye", m = "관리자가 원격 접속을 강제로 해제했습니다." }); await Task.Delay(500); }
            cts.Cancel();
            audio?.Dispose();
            if (admin) { ui.Privacy(blank: false); ui.Privacy(lockInput: false); }
            pc.close();
            ui.SetRemoteActive(false, who);
            try { await _fb.Delete($"pcs/{Pc}/remote"); } catch { }
            Log.Write($"session {reqId}: 종료");
        }
    }

    // Viewer runs on this same PC: the ICE peer address is one of our own IPs (browser host candidates hide
    // behind mDNS, but the nominated pair's remote side is the real source address of the checks).
    // ponytail: via a TURN relay the peer looks like the relay server, so self-connection isn't caught there.
    static bool IsSelf(RTCPeerConnection pc)
    {
        var ip = pc.GetRtpChannel()?.NominatedEntry?.RemoteCandidate?.DestinationEndPoint?.Address;
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(ip));
    }

    Action? _endSession;

    // "해외 IP 차단" (admin page, settings/blockForeign, on unless set to false): reason to refuse this viewer, or null.
    string _answerSdp = "";
    async Task<string?> ForeignBlock(RTCPeerConnection pc)
    {
        try { if ((bool?)await _fb.Get("settings/blockForeign") == false) return null; } catch { }
        return await GeoKr.Check(pc, _answerSdp);
    }
    /// Local user ends the current remote session (banner / tray "원격 해제").
    public void Disconnect() { Log.Write("원격 해제 (이 PC에서)"); _endSession?.Invoke(); }

    // Admin's browser streams the new exe over a DataChannel: {"t":"start",size,version} → binary chunks → {"t":"end"}.
    // Nothing is stored server-side. Applied only when the received file's own version is higher than ours.
    async Task RunUpdate(string reqId)
    {
        var iceServers = await IceServers();
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = ToSipIce(iceServers) });
        var dc = await pc.createDataChannel("update");
        var exe = Environment.ProcessPath!;
        var tmp = exe + ".new";
        var file = File.Create(tmp);
        long size = -1, got = 0;
        var force = false; // admin page "강제 설치": same or older version allowed (reinstall over a stuck PC)
        string? sig = null; // base64 ECDSA signature of the exe (build-agent.ps1 → HtUtil.Agent.exe.sig)
        var pct = -1;
        string? turnMsg = null; // set when the admin sent a TURN key instead of an exe
        var received = new TaskCompletionSource();
        void Reply(bool ok, string msg) { try { dc.send(JsonSerializer.Serialize(new { ok, msg })); } catch { } }
        dc.onmessage += (_, proto, data) =>
        {
            try
            {
                if (proto != DataChannelPayloadProtocols.WebRTC_String)
                {
                    file.Write(data);
                    got += data.Length;
                    var p = size > 0 ? (int)(got * 100 / size) : 0;
                    if (p != pct) { pct = p; ui.UpdateProgress($"받는 중 {got / 1048576.0:0.0} / {size / 1048576.0:0.0} MB ({p}%)", p); }
                    return;
                }
                var m = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(data))!;
                if ((string?)m["t"] == "end") { received.TrySetResult(); return; }
                if ((string?)m["t"] == "turn") { turnMsg = SaveTurn((string?)m["keyId"], (string?)m["token"]); received.TrySetResult(); return; }
                size = (long)m["size"]!;
                var v = ParseVersion((string)m["version"]!);
                force = (bool?)m["force"] ?? false;
                sig = (string?)m["sig"];
                if (v <= Version && !force) throw new Exception($"이미 최신 버전입니다 ({Version.Display()})");
                Log.Write($"업데이트 받는 중: {v.Display()} ({size / 1024} KB)");
                ui.UpdateProgress($"v{v.Display()} 받는 중...", 0);
            }
            catch (Exception e) { received.TrySetException(e); }
        };
        dc.onclose += () => received.TrySetException(new Exception("연결이 끊겼습니다"));
        pc.onconnectionstatechange += s =>
        {
            if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
                received.TrySetException(new Exception("연결이 끊겼습니다"));
        };
        try
        {
            await Offer(pc, reqId, iceServers);
            await WaitConnected(pc, TimeSpan.FromSeconds(20));
            if (await ForeignBlock(pc) is { } why) throw new Exception(why); // updates from abroad too
            await received.Task.WaitAsync(TimeSpan.FromMinutes(10));
            if (turnMsg != null) { Reply(true, turnMsg); await Task.Delay(1000); return; }
            file.Dispose();
            ui.UpdateProgress("받은 파일 확인 중...", 100);
            if (new FileInfo(tmp).Length != size) throw new Exception("파일 크기가 맞지 않습니다");
            // Only exes signed with our build PC's key: a stolen admin account can't push its own program. "force" doesn't skip this.
            if (UpdateKey is not null)
            {
                if (sig is null) throw new Exception("서명 없는 파일은 설치하지 않습니다 (HtUtil.Agent.exe.sig도 같이 선택)");
                bool good;
                using (var f = File.OpenRead(tmp)) good = UpdateKey.VerifyData(f, Convert.FromBase64String(sig), HashAlgorithmName.SHA256);
                if (!good) throw new Exception("서명이 맞지 않습니다 — 설치하지 않음");
                Log.Write("업데이트 서명 확인됨");
            }
            var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(tmp); // trust the file, not the message
            var newVer = new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart);
            if (newVer <= Version && !force) throw new Exception($"받은 파일 버전({newVer.Display()})이 현재({Version.Display()})보다 높지 않습니다");

            // A running exe can be renamed but not overwritten: swap, start the new one, exit.
            // An old .old may still be running (e.g. a service that never restarted): can't delete it, but a running
            // image can be renamed, so move it out of the way; startup cleanup deletes it later.
            try { File.Delete(exe + ".old"); } catch { File.Move(exe + ".old", $"{exe}.old.{DateTime.Now.Ticks}"); }
            File.Move(exe, exe + ".old");
            File.Move(tmp, exe);
            Log.Write($"업데이트 설치: {Version.Display()} → {newVer.Display()}, 재시작");
            Reply(true, $"{Version.Display()} → {newVer.Display()} 설치, 재시작 중");
            ui.UpdateProgress($"v{newVer.Display()} 설치 완료, 재시작 중...", 100);
            await Task.Delay(1000); // let the reply reach the browser
            // Service mode: just exit, the service restarts us (and itself) from the new file.
            // User mode: start the new exe ourselves; "--user" so an unattended PC doesn't get a surprise UAC prompt.
            if (!ServiceMode) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--user") { UseShellExecute = true });
            ui.Quit(goOffline: false);
        }
        catch (Exception e)
        {
            Log.Write($"업데이트 실패: {e.Message}");
            if (size > 0) ui.UpdateProgress($"실패: {e.Message}", -1, closeAfter: 5000); // only when an exe was coming
            Reply(false, e.Message);
            await Task.Delay(1000);
            throw;
        }
        finally
        {
            file.Dispose();
            try { File.Delete(tmp); } catch { }
            pc.close();
        }
    }

    /// Public key for update signatures (embedded at build). Missing = dev build without a key: updates unchecked.
    static readonly ECDsa? UpdateKey = LoadUpdateKey();
    static ECDsa? LoadUpdateKey()
    {
        using var s = typeof(Agent).Assembly.GetManifestResourceStream("update.pub");
        if (s is null) return null;
        var q = Convert.FromBase64String(new StreamReader(s).ReadToEnd().Trim());
        return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = q[..32], Y = q[32..] } });
    }

    public static readonly Version Version = System.Reflection.Assembly.GetEntryAssembly()!.GetName().Version!;
    static Version ParseVersion(string s) => System.Version.Parse(s);

    // Empty keyId = remove (back to STUN only).
    static string SaveTurn(string? keyId, string? token)
    {
        if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(token)) { File.Delete(TurnPath); Log.Write("TURN 설정 삭제"); return "TURN 설정 삭제됨 (STUN만 사용)"; }
        Directory.CreateDirectory(Path.GetDirectoryName(TurnPath)!);
        File.WriteAllText(TurnPath, JsonSerializer.Serialize(new { keyId, token }));
        Log.Write("TURN 설정 저장");
        return "TURN 설정 저장됨";
    }

    // TURN credentials are minted here (agent holds the Cloudflare key), so no Cloud Functions needed.
    // Key: turn.json (pushed from the admin page). Any failure falls back to STUN so a bad key never blocks
    // remote access (or pushing a fixed key).
    static async Task<JsonArray> IceServers()
    {
        JsonArray stun = [new JsonObject { ["urls"] = "stun:stun.cloudflare.com:3478" }];
        try
        {
            if (!File.Exists(TurnPath)) return stun;
            var t = JsonNode.Parse(File.ReadAllText(TurnPath))!;
            var (keyId, token) = ((string?)t["keyId"], (string?)t["token"]);
            if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(token)) return stun;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var r = await http.PostAsJsonAsync(
                $"https://rtc.live.cloudflare.com/v1/turn/keys/{keyId}/credentials/generate-ice-servers", new { ttl = 86400 });
            var j = JsonNode.Parse(await r.EnsureSuccessStatusCode().Content.ReadAsStringAsync())!;
            return j["iceServers"]!.AsArray().DeepClone().AsArray();
        }
        catch (Exception e) { Log.Write($"TURN 발급 실패, STUN만 사용: {e.Message}"); return stun; }
    }

    // SIPSorcery takes one URL per RTCIceServer and no TURN over TLS/TCP, so keep stun: and udp turn: only.
    static List<RTCIceServer> ToSipIce(JsonArray servers) =>
        servers.SelectMany(s =>
        {
            var urls = s!["urls"] is JsonArray a ? a.Select(u => (string)u!) : [(string)s["urls"]!];
            return urls.Where(u => u.StartsWith("stun:") || (u.StartsWith("turn:") && !u.Contains("transport=tcp")))
                .Select(u => new RTCIceServer { urls = u, username = (string?)s["username"], credential = (string?)s["credential"] });
        }).ToList();
}

public static class VersionExt
{
    /// yy.MM.dd.n (build-agent.ps1 stamps it).
    public static string Display(this Version v) => $"{v.Major:00}.{v.Minor:00}.{v.Build:00}.{Math.Max(0, v.Revision)}";
}
