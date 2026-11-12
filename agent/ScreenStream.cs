using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace HtUtil.Agent;

/// Captures one monitor (GDI), encodes VP8 with screen-tuned libvpx settings and sends it on the peer connection.
/// Over the input DataChannel it also tells the viewer the monitor list ({t:"mons"}) and cursor shape ({t:"cur"}).
sealed class ScreenStream(RTCPeerConnection pc, RTCDataChannel dc)
{
    const int Fps = 15, KeyFrameEvery = Fps * 3; // periodic key frame so a lost one recovers within 3s
    volatile int _monitor = Math.Max(0, Array.FindIndex(Screen.AllScreens, s => s.Primary));

    /// Viewer picked another monitor ({t:"mon",i}).
    public void Select(int i)
    {
        if (i < 0 || i >= Screen.AllScreens.Length) return;
        _monitor = i;
        SendMonitors();
    }

    public void SendMonitors() => Send(new
    {
        t = "mons", cur = _monitor,
        list = Screen.AllScreens.Select((s, i) => new { i, w = s.Bounds.Width, h = s.Bounds.Height, primary = s.Primary }),
    });

    void Send(object o) { try { if (dc.readyState == RTCDataChannelState.open) dc.send(JsonSerializer.Serialize(o)); } catch { } }

    // ponytail: GDI CopyFromScreen ~28ms for 1080p (+~13ms encode) → 15fps; DXGI Desktop Duplication if CPU/fps matters.
    public async Task Run(CancellationToken ct)
    {
        VpxVideoEncoder? enc = null; Bitmap? bmp = null; Graphics? g = null;
        var buf = Array.Empty<byte>(); var area = Rectangle.Empty; var codec = default(VideoCodecsEnum);
        string cursor = ""; long sent = 0; int n = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var t0 = Environment.TickCount64;
                try
                {
                    InputDesktop.Sync(); // follow lock screen / UAC (Winlogon desktop) in service mode
                    var screens = Screen.AllScreens;
                    var b = screens[Math.Clamp(_monitor, 0, screens.Length - 1)].Bounds;
                    b.Width &= ~1; b.Height &= ~1; // VP8/I420 needs even sizes
                    if (b != area || enc is null) // first frame or monitor switched: new encoder at the new size
                    {
                        enc?.Dispose(); g?.Dispose(); bmp?.Dispose();
                        area = b;
                        bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
                        g = Graphics.FromImage(bmp);
                        buf = new byte[b.Width * b.Height * 4];
                        enc = new VpxVideoEncoder();
                        codec = enc.SupportedFormats.First().Codec; // encoder's own enum value (see RunSession)
                        n = 0;
                        Input.Area = b;
                        Log.Write($"capture: 모니터 {_monitor + 1} {b.Width}x{b.Height}");
                    }
                    g!.CopyFromScreen(b.X, b.Y, 0, 0, b.Size);
                    var d = bmp!.LockBits(new Rectangle(Point.Empty, b.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                    bmp.UnlockBits(d);

                    if (n % KeyFrameEvery == 1) enc.ForceKeyFrame(); // frame 0 creates the native encoder, frame 1 = first tuned key frame
                    var frame = enc.EncodeVideo(b.Width, b.Height, buf, VideoPixelFormatsEnum.Bgra, codec);
                    if (n++ == 0) VpxTuning.Apply(enc);
                    if (frame is { Length: > 0 })
                    {
                        pc.SendVideo(90000 / Fps, frame); // duration in 90kHz RTP units
                        if (++sent == 1 || sent % 300 == 0) Log.Write($"video: {sent} frames sent ({frame.Length} bytes)");
                    }
                    var c = CursorCss();
                    if (c != cursor) { cursor = c; Send(new { t = "cur", c }); }
                }
                catch (Exception e) { Log.Write($"capture: {e.Message}"); } // e.g. secure desktop (UAC / lock screen)
                await Task.Delay(Math.Max(1, 1000 / Fps - (int)(Environment.TickCount64 - t0)), ct).ContinueWith(_ => { });
            }
        }
        finally { enc?.Dispose(); g?.Dispose(); bmp?.Dispose(); }
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
