using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HtUtil.Agent;

/// DXGI Desktop Duplication of one monitor: the GPU tells us when the screen changed, and copying a frame costs
/// a few ms instead of GDI's ~28 ms for 1080p. Throws on anything unusual (secure desktop, mode change, remote
/// session without a GPU…); ScreenStream then falls back to GDI and retries later.
sealed class Dxgi : IDisposable
{
    public readonly Rectangle Area;
    readonly ID3D11Device _dev;
    readonly ID3D11DeviceContext _ctx;
    readonly IDXGIOutputDuplication _dup;
    readonly ID3D11Texture2D _staging;
    bool _first = true;

    public Dxgi(Rectangle area)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        using (adapter)
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            using (output)
            {
                var r = output.Description.DesktopCoordinates;
                if (new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) is var b && b.Location != area.Location) continue;
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null!, out _dev!, out _ctx!).CheckError();
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                _dup = output1.DuplicateOutput(_dev);
                Area = area;
                _staging = _dev.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)area.Width, Height = (uint)area.Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                });
                return;
            }
        throw new Exception("DXGI 출력 없음");
    }

    /// Copies the newest frame into buf (BGRA, Area size, tightly packed). false = nothing changed within timeoutMs.
    public bool Grab(byte[] buf, int timeoutMs)
    {
        var hr = _dup.AcquireNextFrame((uint)timeoutMs, out var info, out var res);
        if (hr == Vortice.DXGI.ResultCode.WaitTimeout) return false;
        hr.CheckError(); // AccessLost (desktop switch, resolution change) → caller recreates
        try
        {
            // LastPresentTime 0 = only the mouse moved (cursor isn't in the frame anyway) — but the first frame is the whole desktop
            if (info.LastPresentTime == 0 && !_first) return false;
            _first = false;
            using var tex = res.QueryInterface<ID3D11Texture2D>();
            // Duplication texture can be larger than our even-sized area: copy just the area
            _ctx.CopySubresourceRegion(_staging, 0, 0, 0, 0, tex, 0, new Vortice.Mathematics.Box(0, 0, 0, Area.Width, Area.Height, 1));
        }
        finally { res.Dispose(); _dup.ReleaseFrame(); }
        var map = _ctx.Map(_staging, 0, MapMode.Read);
        try
        {
            int row = Area.Width * 4;
            for (int y = 0; y < Area.Height; y++) Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), buf, y * row, row);
        }
        finally { _ctx.Unmap(_staging, 0); }
        return true;
    }

    public void Dispose() { _staging?.Dispose(); _dup?.Dispose(); _ctx?.Dispose(); _dev?.Dispose(); }
}
