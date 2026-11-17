using System.Net;
using System.Net.Sockets;
using SIPSorcery.Net;

namespace HtUtil.Agent;

/// "해외 IP 차단": is the viewer connecting from Korea? Uses APNIC's public delegation list (which address blocks are
/// assigned to KR), downloaded weekly and cached — no account or API key. Checked on the agent (where remote control
/// actually happens), so a modified web page can't skip it.
static class GeoKr
{
    const string Url = "https://ftp.apnic.net/stats/apnic/delegated-apnic-latest";
    static string CachePath => Path.Combine(Agent.ServiceMode ? Installer.DataDir : Path.GetDirectoryName(Agent.UserTurnPath)!, "geo-kr.txt");
    static (uint start, uint end)[] _v4 = [];
    static (byte[] prefix, int bits)[] _v6 = [];
    static DateTime _loaded;
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// null = list unavailable (no cache and download failed).
    static async Task<bool> EnsureLoaded()
    {
        await Gate.WaitAsync();
        try
        {
            if (_v4.Length > 0 && DateTime.UtcNow - _loaded < TimeSpan.FromDays(1)) return true;
            var path = CachePath;
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(7))
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    var lines = (await http.GetStringAsync(Url)).Split('\n').Where(l => l.StartsWith("apnic|KR|ipv")).ToArray();
                    if (lines.Length > 100) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllLinesAsync(path, lines); Log.Write($"국가 IP 목록 갱신 ({lines.Length}개)"); }
                }
                catch (Exception e) { Log.Write($"국가 IP 목록 받기 실패: {e.Message}"); } // keep using the old cache
            if (!File.Exists(path)) return false;
            var v4 = new List<(uint, uint)>(); var v6 = new List<(byte[], int)>();
            foreach (var l in File.ReadLines(path))
            {
                var f = l.Split('|'); // apnic|KR|ipv4|1.11.0.0|65536|...   apnic|KR|ipv6|2001:2d8::|32|...
                if (f.Length < 5 || !IPAddress.TryParse(f[3], out var ip) || !long.TryParse(f[4], out var n)) continue;
                if (f[2] == "ipv4") { var s = V4(ip); v4.Add((s, (uint)Math.Min(uint.MaxValue, s + n - 1))); }
                else if (f[2] == "ipv6") v6.Add((ip.GetAddressBytes(), (int)n));
            }
            _v4 = v4.OrderBy(x => x.Item1).ToArray(); _v6 = v6.ToArray(); _loaded = DateTime.UtcNow;
            return _v4.Length > 0;
        }
        finally { Gate.Release(); }
    }

    static uint V4(IPAddress ip) { var b = ip.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }

    static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && (b[1] & 0xC0) == 64); // 100.64/10 carrier NAT: never routed between countries
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 ULA
    }

    static bool InKorea(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var v = V4(ip);
            int lo = 0, hi = _v4.Length - 1; // binary search the sorted ranges
            while (lo <= hi) { var m = (lo + hi) / 2; if (v < _v4[m].start) hi = m - 1; else if (v > _v4[m].end) lo = m + 1; else return true; }
            return false;
        }
        var a = ip.GetAddressBytes();
        foreach (var (p, bits) in _v6)
        {
            int full = bits / 8, rest = bits % 8; var ok = true;
            for (int i = 0; i < full && ok; i++) ok = a[i] == p[i];
            if (ok && rest > 0) ok = (a[full] >> (8 - rest)) == (p[full] >> (8 - rest));
            if (ok) return true;
        }
        return false;
    }

    /// Reason to refuse the viewer, or null if it's from Korea / the same LAN. Looks at the connected address and at the
    /// public addresses the browser listed in its answer (needed when the path goes through a TURN relay, where the
    /// connected address is the relay's).
    public static async Task<string?> Check(RTCPeerConnection pc, string answerSdp)
    {
        var ips = new List<IPAddress>();
        var remote = pc.GetRtpChannel()?.NominatedEntry?.RemoteCandidate;
        if (remote is { type: not RTCIceCandidateType.relay, DestinationEndPoint.Address: { } a }) ips.Add(a);
        foreach (var line in answerSdp.Split('\n'))
        {
            // a=candidate:<foundation> <component> <proto> <priority> <address> <port> typ <type> ...
            var f = line.Trim().Split(' ');
            if (f.Length > 7 && f[0].StartsWith("a=candidate") && f[7] is "host" or "srflx" or "prflx" && IPAddress.TryParse(f[4], out var ip)) ips.Add(ip);
        }
        var pub = ips.Where(ip => !IsPrivate(ip)).Distinct().ToList();
        if (pub.Count == 0) return remote?.type == RTCIceCandidateType.relay ? "접속 위치를 확인할 수 없어 거부했습니다 (해외 IP 차단 중)" : null; // LAN = fine
        if (!await EnsureLoaded()) return "국가 IP 목록을 받지 못해 접속을 거부했습니다 (해외 IP 차단 중)";
        var foreign = pub.FirstOrDefault(ip => !InKorea(ip));
        return foreign is null ? null : $"해외 IP({foreign})에서의 접속은 차단되어 있습니다";
    }
}
