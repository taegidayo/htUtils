using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SIPSorcery.Net;

namespace HtUtil.Agent;

/// Viewer drops files on the remote screen. "file" DataChannel, one file at a time:
///   {"t":"file",name,size} → binary chunks → {"t":"end"}   ⇒ reply {"t":"saved",name} | {"t":"error",msg}
/// Saved to this PC's Desktop with a unique name; directories in the name are stripped.
static class FileDrop
{
    public static void Attach(RTCDataChannel dc, MainForm ui)
    {
        FileStream? fs = null;
        string path = "";
        long size = 0;
        void Reply(object o) { try { dc.send(JsonSerializer.Serialize(o)); } catch { } }
        void Abort() { fs?.Dispose(); fs = null; try { if (path != "") File.Delete(path); } catch { } }

        dc.onmessage += (_, proto, data) =>
        {
            try
            {
                if (proto != DataChannelPayloadProtocols.WebRTC_String) { fs?.Write(data); return; }
                var m = JsonNode.Parse(Encoding.UTF8.GetString(data))!;
                switch ((string?)m["t"])
                {
                    case "file":
                        Abort();
                        path = UniquePath((string)m["name"]!);
                        size = (long)m["size"]!;
                        fs = File.Create(path);
                        break;
                    case "end" when fs is not null:
                        var len = fs.Length;
                        fs.Dispose(); fs = null;
                        if (len != size) { Abort(); Reply(new { t = "error", msg = $"크기가 맞지 않음 ({len}/{size})" }); return; }
                        Log.Write($"파일 받음: {path}");
                        ui.Notify("파일을 받았습니다", Path.GetFileName(path) + " (바탕화면)");
                        Reply(new { t = "saved", name = Path.GetFileName(path) });
                        path = "";
                        break;
                }
            }
            catch (Exception e) { Abort(); Reply(new { t = "error", msg = e.Message }); }
        };
        dc.onclose += Abort; // session ended mid-file: don't leave a partial file behind
    }

    static string UniquePath(string name)
    {
        name = string.Concat(Path.GetFileName(name).Split(Path.GetInvalidFileNameChars()));
        if (name.Trim('.', ' ') == "") name = "file";
        // service mode runs as SYSTEM: its own "Desktop" is the system profile's, so use the console user's
        var dir = Agent.ServiceMode ? Native.ConsoleUserDesktop() : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var p = Path.Combine(dir, name);
        for (var i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return p;
    }
}
