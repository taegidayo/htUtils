using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace HtUtil.Agent;

// agent.json holds only values shared by every PC; per-PC identity is created on first run.
public record Config(string ApiKey, string DatabaseUrl, string? TurnKeyId = null, string? TurnApiToken = null);

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
        _ = Task.Delay(10_000).ContinueWith(_ => { try { File.Delete(Environment.ProcessPath + ".old"); } catch { } });

        _ = Loop(TimeSpan.FromSeconds(30), () => _fb.Patch($"pcs/{Pc}", new { online = true, lastSeen = Firebase.ServerTime }), ct);
        _ = Loop(TimeSpan.FromMinutes(5), NewCode, ct);
        await _fb.Listen($"requests/{Pc}", (_, _) => _ = OnRequestsChanged(), ct);
    }

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

    static async Task Loop(TimeSpan every, Func<Task> f, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await f(); } catch (Exception e) { Log.Write(e.Message); }
            await Task.Delay(every, ct).ContinueWith(_ => { });
        }
    }

    async Task NewCode()
    {
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
                _ = Handle(id, (string)r!["uid"]!, (string)r["mode"]!);
    }

    async Task Handle(string reqId, string uid, string mode)
    {
        var reqPath = $"requests/{Pc}/{reqId}";
        try
        {
            if (!await _busy.WaitAsync(0)) { await _fb.Patch(reqPath, new { status = "denied", reason = "busy" }); return; }
            try
            {
                if (!await Allowed(uid, mode)) { await _fb.Patch(reqPath, new { status = "denied" }); return; }
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
    async Task<bool> Allowed(string uid, string mode)
    {
        if (mode == "guest") return ui.Ask($"게스트가 이 PC의 원격 제어를 요청했습니다.\n허용하시겠습니까?");
        var user = await _fb.Get($"users/{uid}");
        var level = (int?)user?["level"] ?? 0;
        if (mode == "update") return level == 9;
        if (mode != "account") return false;
        var minLevel = (int?)await _fb.Get($"pcs/{Pc}/minLevel") ?? 1;
        if (level < Math.Max(1, minLevel)) return false;
        return level >= 2 || ui.Ask($"{user?["name"]} 님이 원격 제어를 요청했습니다.\n허용하시겠습니까?");
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
        var iceServers = await IceServers();
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = ToSipIce(iceServers) });
        // SIPSorceryMedia.Encoders 10.0.4 (latest) is built against Abstractions 8.0.12, but SIPSorcery 10 loads
        // Abstractions 10, whose VideoCodecsEnum is shifted by one (Unknown=0 added): the encoder's "VP8" (7) reads
        // as H263 → offer says H263 → Chrome rejects video → setRemoteDescription = VideoIncompatible.
        // So: advertise VP8 with SIPSorcery 10's enum here; ScreenStream gives the encoder its own value.
        pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.VP8, 96), MediaStreamStatusEnum.SendOnly));
        var dc = await pc.createDataChannel("input");
        FileDrop.Attach(await pc.createDataChannel("file"), ui); // separate channel: big files don't delay mouse/keys
        var stream = new ScreenStream(pc, dc);
        var lastMsg = DateTime.UtcNow; // viewer sends {t:"hb"} every 2s; silence = browser gone (tab closed, crash)
        dc.onmessage += (_, _, data) =>
        {
            lastMsg = DateTime.UtcNow;
            try
            {
                var m = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(data))!;
                if ((string?)m["t"] == "mon") stream.Select((int)m["i"]!); else Input.Handle(m);
            }
            catch { }
        };
        dc.onopen += () => { Log.Write($"session {reqId}: 입력 채널 열림"); stream.SendMonitors(); };

        var done = new TaskCompletionSource();
        var cts = new CancellationTokenSource();
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
            lastMsg = DateTime.UtcNow; // heartbeat window starts now
            _ = stream.Run(cts.Token);
            ui.SetRemoteActive(true);
            // Ends on: viewer hang-up / tab close (DataChannel closes), no heartbeat for 10s, connection loss, local "해제".
            while (!done.Task.IsCompleted)
            {
                await Task.WhenAny(done.Task, Task.Delay(2000));
                if (DateTime.UtcNow - lastMsg > TimeSpan.FromSeconds(10)) { Log.Write($"session {reqId}: 뷰어 응답 없음 (10초), 종료"); break; }
            }
        }
        finally
        {
            _endSession = null;
            cts.Cancel();
            pc.close();
            ui.SetRemoteActive(false);
            Log.Write($"session {reqId}: 종료");
        }
    }

    Action? _endSession;
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
        long size = -1;
        var received = new TaskCompletionSource();
        void Reply(bool ok, string msg) { try { dc.send(JsonSerializer.Serialize(new { ok, msg })); } catch { } }
        dc.onmessage += (_, proto, data) =>
        {
            try
            {
                if (proto != DataChannelPayloadProtocols.WebRTC_String) { file.Write(data); return; }
                var m = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(data))!;
                if ((string?)m["t"] == "end") { received.TrySetResult(); return; }
                size = (long)m["size"]!;
                var v = ParseVersion((string)m["version"]!);
                if (v <= Version) throw new Exception($"이미 최신 버전입니다 ({Version.Display()})");
                Log.Write($"업데이트 받는 중: {v.Display()} ({size / 1024} KB)");
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
            await received.Task.WaitAsync(TimeSpan.FromMinutes(10));
            file.Dispose();
            if (new FileInfo(tmp).Length != size) throw new Exception("파일 크기가 맞지 않습니다");
            var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(tmp); // trust the file, not the message
            var newVer = new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart);
            if (newVer <= Version) throw new Exception($"받은 파일 버전({newVer.Display()})이 현재({Version.Display()})보다 높지 않습니다");

            // A running exe can be renamed but not overwritten: swap, start the new one, exit.
            File.Delete(exe + ".old");
            File.Move(exe, exe + ".old");
            File.Move(tmp, exe);
            Log.Write($"업데이트 설치: {Version.Display()} → {newVer.Display()}, 재시작");
            Reply(true, $"{Version.Display()} → {newVer.Display()} 설치, 재시작 중");
            await Task.Delay(1000); // let the reply reach the browser
            // Service mode: just exit, the service restarts us (and itself) from the new file.
            // User mode: start the new exe ourselves; "--user" so an unattended PC doesn't get a surprise UAC prompt.
            if (!ServiceMode) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--user") { UseShellExecute = true });
            ui.Quit(goOffline: false);
        }
        catch (Exception e)
        {
            Log.Write($"업데이트 실패: {e.Message}");
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

    public static readonly Version Version = System.Reflection.Assembly.GetEntryAssembly()!.GetName().Version!;
    static Version ParseVersion(string s) => System.Version.Parse(s);

    // TURN credentials are minted here (agent holds the Cloudflare key), so no Cloud Functions needed.
    async Task<JsonArray> IceServers()
    {
        if (string.IsNullOrEmpty(cfg.TurnKeyId) || string.IsNullOrEmpty(cfg.TurnApiToken))
            return [new JsonObject { ["urls"] = "stun:stun.cloudflare.com:3478" }];
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", cfg.TurnApiToken);
        var r = await http.PostAsJsonAsync(
            $"https://rtc.live.cloudflare.com/v1/turn/keys/{cfg.TurnKeyId}/credentials/generate-ice-servers", new { ttl = 86400 });
        var j = JsonNode.Parse(await r.EnsureSuccessStatusCode().Content.ReadAsStringAsync())!;
        return j["iceServers"]!.AsArray().DeepClone().AsArray();
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
