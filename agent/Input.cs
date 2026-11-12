using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace HtUtil.Agent;

/// Injects viewer input. Messages (JSON over DataChannel):
///  {t:"mm",x,y} x/y 0..1 on the streamed monitor | {t:"md"|"mu",b:0|1|2} | {t:"wh",d:deltaY} | {t:"kd"|"ku",k:keyCode}
///  {t:"txt",s} typed text (mobile keyboard), injected as Unicode
public static class Input
{
    /// Monitor currently being streamed (physical pixels, virtual-desktop coordinates). Set by ScreenStream.
    public static Rectangle Area = Screen.PrimaryScreen!.Bounds;

    public static void Handle(JsonNode m)
    {
        switch ((string?)m["t"])
        {
            case "mm":
                // VIRTUALDESK: 0..65535 spans all monitors, so map the 0..1 position inside Area onto that range.
                var vs = SystemInformation.VirtualScreen;
                double px = Area.X + (double)m["x"]! * (Area.Width - 1), py = Area.Y + (double)m["y"]! * (Area.Height - 1);
                Mouse(MOVE | ABSOLUTE | VIRTUALDESK, 0,
                    (int)Math.Round((px - vs.X) * 65535.0 / Math.Max(1, vs.Width - 1)),
                    (int)Math.Round((py - vs.Y) * 65535.0 / Math.Max(1, vs.Height - 1)));
                break;
            case "md": Mouse((int)m["b"]! switch { 1 => MDOWN, 2 => RDOWN, _ => LDOWN }); break;
            case "mu": Mouse((int)m["b"]! switch { 1 => MUP, 2 => RUP, _ => LUP }); break;
            case "wh": Mouse(WHEEL, -(int)(double)m["d"]!); break;
            case "kd": Key((ushort)(int)m["k"]!, false); break;
            case "ku": Key((ushort)(int)m["k"]!, true); break;
            // Mobile soft keyboards don't give usable key codes (229 while composing Korean etc.), so the viewer sends
            // the typed text and we inject it as Unicode characters: works regardless of the remote 한/영 state.
            case "txt":
                foreach (var c in (string)m["s"]!)
                    if (c == '\n') { Key(13, false); Key(13, true); }
                    else { Unicode(c, false); Unicode(c, true); }
                break;
        }
    }

    static void Unicode(char c, bool up) =>
        Send(new INPUT { type = 1, u = new() { ki = new() { wVk = 0, wScan = c, dwFlags = 4u /* KEYEVENTF_UNICODE */ | (up ? 2u : 0u) } } });

    // ponytail: browser keyCode ~= Windows VK for common keys; map KeyboardEvent.code if odd keys/layouts misbehave.
    static void Key(ushort vk, bool up)
    {
        uint flags = up ? 2u : 0u; // KEYEVENTF_KEYUP
        if (vk is >= 33 and <= 46 or 91 or 92 or 93 or 111 or 144) flags |= 1; // EXTENDEDKEY: nav keys, win, numpad /, numlock
        Send(new INPUT { type = 1, u = new() { ki = new() { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags } } });
    }

    static void Mouse(uint flags, int data = 0, int x = 0, int y = 0) =>
        Send(new INPUT { type = 0, u = new() { mi = new() { dx = x, dy = y, mouseData = data, dwFlags = flags } } });

    static void Send(INPUT i)
    {
        InputDesktop.Sync(); // input goes to the thread's desktop: follow lock screen / UAC in service mode
        SendInput(1, [i], Marshal.SizeOf<INPUT>());
    }

    const uint MOVE = 1, LDOWN = 2, LUP = 4, RDOWN = 8, RUP = 0x10, MDOWN = 0x20, MUP = 0x40, WHEEL = 0x800, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public U u; }
    [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
}
