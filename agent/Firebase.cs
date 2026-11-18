using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HtUtil.Agent;

/// Firebase Auth + Realtime Database over REST/SSE (no official .NET client SDK exists).
public sealed class Firebase(string apiKey, string dbUrl)
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly string _db = dbUrl.TrimEnd('/');
    string _idToken = "", _refreshToken = "";
    DateTime _expires;
    public string Uid { get; private set; } = "";

    public static readonly JsonObject ServerTime = new() { [".sv"] = "timestamp" };

    public string RefreshToken => _refreshToken;

    /// Creates a new anonymous account.
    public Task SignUpAnonymously() => Account("signUp", new { returnSecureToken = true });

    async Task Account(string op, object body)
    {
        var r = await Http.PostAsJsonAsync($"https://identitytoolkit.googleapis.com/v1/accounts:{op}?key={apiKey}", body);
        var j = (await Ok(r))!;
        Uid = (string)j["localId"]!;
        SetToken((string)j["idToken"]!, (string)j["refreshToken"]!, (string)j["expiresIn"]!);
    }

    /// Signs back into an existing (anonymous) account; the refresh token is its only credential.
    public async Task SignIn(string refreshToken)
    {
        _refreshToken = refreshToken; _expires = default;
        await Token();
    }

    async Task<string> Token()
    {
        if (DateTime.UtcNow < _expires) return _idToken;
        var r = await Http.PostAsync($"https://securetoken.googleapis.com/v1/token?key={apiKey}",
            new FormUrlEncodedContent(new Dictionary<string, string>
                { ["grant_type"] = "refresh_token", ["refresh_token"] = _refreshToken }));
        var j = (await Ok(r))!;
        Uid = (string)j["user_id"]!;
        SetToken((string)j["id_token"]!, (string)j["refresh_token"]!, (string)j["expires_in"]!);
        return _idToken;
    }

    void SetToken(string id, string refresh, string expiresIn)
    {
        _idToken = id; _refreshToken = refresh;
        _expires = DateTime.UtcNow.AddSeconds(int.Parse(expiresIn) - 300);
    }

    async Task<string> Url(string path) => $"{_db}/{path}.json?auth={await Token()}";

    public async Task<JsonNode?> Get(string path) => await Ok(await Http.GetAsync(await Url(path)));
    public async Task Put(string path, object? v) => await Ok(await Http.PutAsJsonAsync(await Url(path), v));
    public async Task Patch(string path, object v) => await Ok(await Http.PatchAsJsonAsync(await Url(path), v));
    public async Task Post(string path, object v) => await Ok(await Http.PostAsJsonAsync(await Url(path), v));
    public async Task Delete(string path) => await Ok(await Http.DeleteAsync(await Url(path)));

    static async Task<JsonNode?> Ok(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Exception($"{(int)r.StatusCode} {body}");
        return JsonNode.Parse(body);
    }

    /// Streams `path` (SSE). onEvent(relativePath, data) for every put/patch. Reconnects until ct cancels.
    public async Task Listen(string path, Action<string, JsonNode?> onEvent, CancellationToken ct)
    {
        var down = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, await Url(path));
                req.Headers.Accept.ParseAdd("text/event-stream");
                using var r = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                r.EnsureSuccessStatusCode();
                down = false;
                // Firebase sends keep-alive every ~30s. After sleep / Wi-Fi drop the TCP connection can die silently and the
                // read would wait forever (agent shows online via heartbeat but never sees remote requests) -> reconnect.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(75));
                using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
                string? ev = null;
                while (await reader.ReadLineAsync(idle.Token) is { } line)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(75));
                    if (line.StartsWith("event: ")) ev = line[7..];
                    else if (line.StartsWith("data: ") && ev is "put" or "patch")
                    {
                        var d = JsonNode.Parse(line[6..])!;
                        onEvent((string)d["path"]!, d["data"]);
                    }
                    else if (ev is "auth_revoked" or "cancel") break; // reconnect with fresh token
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { Log.Write($"listen {path}: 응답 없음 — 다시 연결"); }
            catch (HttpRequestException) when (down) { } // network still down: already logged once
            catch (Exception e) { down = e is HttpRequestException; Log.Write($"listen {path}: {e.Message}"); }
            await Task.Delay(2000, ct).ContinueWith(_ => { });
        }
    }
}

public static class Log
{
    public static event Action<string>? Line;
    public static void Write(string s) => Line?.Invoke($"{DateTime.Now:HH:mm:ss} {s}");
}
