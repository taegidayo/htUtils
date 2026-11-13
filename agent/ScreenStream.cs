using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace HtUtil.Agent;

/// Captures one monitor or the whole virtual desktop (GDI), encodes VP8 with screen-tuned libvpx settings and sends it on the peer connection.
/// Over the input DataChannel it also tells the viewer the monitor list ({t:"mons"}) and cursor shape ({t:"cur"}).
sealed class ScreenStream(RTCPeerConnection pc, RTCDataChannel dc)
{
    const int MaxFps = 30;
    public const int All = -1; // whole virtual desktop: the viewer splits it into one window per monitor
    volatile int _monitor = Math.Max(0, Array.FindIndex(Screen.AllScreens, s => s.Primary));

    /// Viewer picked another monitor ({t:"mon",i}), or All (-1). Only the picked area is captured/encoded.
    public void Select(int i)
    {
        if (i != All && (i < 0 || i >= Screen.AllScreens.Length)) return;
        _monitor = i;
        SendMonitors();
    }

    /// x/y are virtual-desktop coordinates; vx/vy/vw/vh = the All frame (virtual desktop) so the viewer can crop.
    public void SendMonitors()
    {
        var v = SystemInformation.VirtualScreen;
        Send(new
        {
            t = "mons", cur = _monitor, vx = v.X, vy = v.Y, vw = v.Width & ~1, vh = v.Height & ~1,
            list = Screen.AllScreens.Select((s, i) => new { i, x = s.Bounds.X, y = s.Bounds.Y, w = s.Bounds.Width, h = s.Bounds.Height, primary = s.Primary }),
        });
    }

    void Send(object o) { try { if (dc.readyState == RTCDataChannelState.open) dc.send(JsonSerializer.Serialize(o)); } catch { } }

    // Single monitor: DXGI Desktop Duplication (only changed frames, ~30fps). All monitors / secure desktop / no GPU:
    // GDI CopyFromScreen (~28ms for 1080p → ~15fps). An unchanged screen is re-sent once a second (and as a key frame
    // every 3s) so a lost packet still heals.
    public Task Run(CancellationToken ct) => Task.Run(() =>
    {
        while (!ct.IsCancellationRequested)
        {
            var t = new Thread(() => Loop(ct)) { IsBackground = true, Name = "capture" };
            t.Start(); t.Join(); // returns when the input desktop changed → next thread starts on the new one
        }
    });

    void Loop(CancellationToken ct)
    {
        var desk = InputDesktop.Sync(); // fresh thread: always allowed to switch
        if (desk != "Default") Log.Write($"capture: {desk} 화면 (GDI)");
        VpxVideoEncoder? enc = null; Bitmap? bmp = null; Graphics? g = null; Dxgi? dx = null;
        var buf = Array.Empty<byte>(); var area = Rectangle.Empty; var codec = default(VideoCodecsEnum);
        string cursor = ""; long sent = 0, lastSent = 0, lastKey = 0, dxRetry = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var t0 = Environment.TickCount64;
                try
                {
                    // lock screen / Ctrl+Alt+Del / UAC = "Winlogon" desktop: end this thread, Run starts one there.
                    // DXGI only on the normal desktop (on the secure one it can just stop delivering frames) → GDI there.
                    if (InputDesktop.Current() is var now0 && now0 != desk && now0 != "?") return;
                    var screens = Screen.AllScreens;
                    // ponytail: All = one frame of the whole virtual desktop (~2x CPU/bandwidth for 2 monitors, gaps
                    // between differently sized monitors are captured as black); per-monitor tracks if that matters.
                    var b = _monitor == All ? SystemInformation.VirtualScreen : screens[Math.Clamp(_monitor, 0, screens.Length - 1)].Bounds;
                    b.Width &= ~1; b.Height &= ~1; // VP8/I420 needs even sizes
                    if (b != area || enc is null) // first frame or monitor switched: new encoder at the new size
                    {
                        enc?.Dispose(); g?.Dispose(); bmp?.Dispose(); dx?.Dispose(); dx = null; dxRetry = 0;
                        area = b;
                        bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
                        g = Graphics.FromImage(bmp);
                        buf = new byte[b.Width * b.Height * 4];
                        enc = new VpxVideoEncoder();
                        codec = enc.SupportedFormats.First().Codec; // encoder's own enum value (see RunSession)
                        sent = 0; lastSent = 0;
                        Input.Area = b;
                        Log.Write($"capture: {(_monitor == All ? "모든 모니터" : $"모니터 {_monitor + 1}")} {b.Width}x{b.Height}");
                    }
                    // ponytail: rotated (portrait) monitors come out rotated from DXGI → they'd need a rotate; GDI is used if DXGI throws.
                    if (dx is null && _monitor != All && t0 >= dxRetry && desk == "Default")
                        try { dx = new Dxgi(b); Log.Write("capture: DXGI"); }
                        catch (Exception e) { dxRetry = t0 + 5000; Log.Write($"capture: GDI 사용 (DXGI 실패: {e.Message})"); }
                    bool changed;
                    if (dx is not null)
                        try { changed = dx.Grab(buf, sent == 0 ? 200 : 30); }
                        catch (Exception e) { dx.Dispose(); dx = null; dxRetry = t0 + 2000; Log.Write($"capture: DXGI 다시 시작 ({e.Message})"); continue; }
                    else
                    {
                        g!.CopyFromScreen(b.X, b.Y, 0, 0, b.Size);
                        var d = bmp!.LockBits(new Rectangle(Point.Empty, b.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                        bmp.UnlockBits(d);
                        changed = true;
                    }

                    var now = Environment.TickCount64;
                    if (changed || sent == 0 || now - lastSent >= 1000)
                    {
                        if (sent == 1 || now - lastKey >= 3000) { enc.ForceKeyFrame(); lastKey = now; } // frame 0 creates the native encoder
                        var frame = enc.EncodeVideo(b.Width, b.Height, buf, VideoPixelFormatsEnum.Bgra, codec);
                        if (sent == 0) VpxTuning.Apply(enc);
                        if (frame is { Length: > 0 })
                        {
                            // RTP timestamp = wall clock (90 kHz). Frames are sent irregularly (only on change), and SendVideo's
                            // duration only advances the timestamp *after* the frame: passing the gap there made every timestamp
                            // one gap behind → Chrome saw "late" frames and grew its jitter buffer to seconds (v26.11.13.15).
                            var tr = pc.VideoStream.LocalTrack;
                            if (RtpClock.Set(tr, (uint)(now * 90))) pc.SendVideo(0, frame);
                            else pc.SendVideo(90000 / MaxFps, frame); // setter missing (package changed): old fixed step
                            if (sent % 900 == 1) Log.Write($"video: {sent} frames sent ({frame.Length} bytes)");
                        }
                        sent++; lastSent = now;
                    }
                    var c = CursorCss();
                    if (c != cursor) { cursor = c; Send(new { t = "cur", c }); }
                }
                catch (Exception e) { Log.Write($"capture: {e.Message}"); } // e.g. secure desktop (UAC / lock screen)
                // DXGI waits inside Grab; both paced to MaxFps
                var left = 1000 / MaxFps - (int)(Environment.TickCount64 - t0);
                if (left > 0) ct.WaitHandle.WaitOne(left);
            }
        }
        finally { enc?.Dispose(); g?.Dispose(); bmp?.Dispose(); dx?.Dispose(); }
    }

    // Remote cursor shape → CSS cursor for the viewer's own pointer (instant, no latency), like Chrome Remote Desktop.
    static readonly (IntPtr h, string css)[] Cursors = new (int id, string css)[]
    {
        (32512, "default"), (32513, "text"), (32514, "wait"), (32515, "crosshair"), (32642, "nwse-resize"), (32643, "nesw-resize"),
        (32644, "ew-resize"), (32645, "ns-resize"), (32646, "move"), (32648, "not-allowed"), (32649, "pointer"), (32650, "progress"), (32651, "help"),
    }.Select(x => (LoadCursor(IntPtr.Zero, x.id), x.css)).ToArray();

    static string CursorCss()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci)) return "default";
        foreach (var (h, css) in Cursors) if (h == ci.hCursor) return css;
        return "default"; // app-specific custom cursor
    }

    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr hInstance, int id);
}

/// MediaStreamTrack.Timestamp has no public setter in SIPSorcery 10.0.17; we need it to stamp frames with real time.
static class RtpClock
{
    static readonly Action<MediaStreamTrack, uint>? Setter = Make();
    static Action<MediaStreamTrack, uint>? Make()
    {
        // ponytail: non-public member of SIPSorcery 10.0.17 — re-check if the package is upgraded (Set then returns false).
        var p = typeof(MediaStreamTrack).GetProperty("Timestamp", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p?.GetSetMethod(true) is { } m) return (t, v) => m.Invoke(t, [v]);
        var f = typeof(MediaStreamTrack).GetField("<Timestamp>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        return f == null ? null : (t, v) => f.SetValue(t, v);
    }
    public static bool Set(MediaStreamTrack t, uint v) { if (Setter == null) return false; Setter(t, v); return true; }
}

/// SIPSorcery's VP8 encoder defaults (VBR 256 kbps, max quantizer 63) make text blurry. Measured on a 1920x1080
/// desktop: key frame 22 KB @ q63 → 99 KB @ q≤30, capture+encode time unchanged (~41 ms).
static class VpxTuning
{
    // vpx_codec_control_ directly: the package's typed control wrappers have broken entry points.
    // IDs from vp8cx.h: VP8E_SET_CPUUSED=13, VP8E_SET_CQ_LEVEL=25, VP8E_SET_MAX_INTRA_BITRATE_PCT=26, VP8E_SET_SCREEN_CONTENT_MODE=31
    [DllImport("vpxmd", EntryPoint = "vpx_codec_control_")] static extern int Control(IntPtr ctx, int id, int value);

    public static void Apply(VpxVideoEncoder enc)
    {
        try
        {
            // ponytail: private fields of SIPSorceryMedia.Encoders 10.0.4 — re-check if the package is upgraded.
            const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
            var vp8 = typeof(VpxVideoEncoder).GetField("_vp8Encoder", P)!.GetValue(enc)!;
            var ctx = (vpxmd.VpxCodecCtx)vp8.GetType().GetField("_vpxEncodeCtx", P)!.GetValue(vp8)!;
            var c = ctx.config.Enc;
            c.RcEndUsage = vpxmd.VpxRcMode.VPX_CQ; c.RcTargetBitrate = 8000; c.RcMinQuantizer = 2; c.RcMaxQuantizer = 30;
            c.GThreads = (uint)Math.Min(4, Environment.ProcessorCount); c.RcDropframeThresh = 0;
            var r = vpxmd.vpx_encoder.VpxCodecEncConfigSet(ctx, c);
            Control(ctx.__Instance, 25, 8);  // CQ level
            Control(ctx.__Instance, 31, 1);  // screen content mode
            Control(ctx.__Instance, 13, -8); // realtime speed
            Control(ctx.__Instance, 26, 0);  // no cap on key frame size
            Log.Write($"vp8 화질 설정: {r}");
        }
        catch (Exception e) { Log.Write($"vp8 화질 설정 실패 (기본값 사용): {e.Message}"); }
    }
}
