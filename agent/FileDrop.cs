using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SIPSorcery.Net;

namespace HtUtil.Agent;

/// Viewer drops files on the remote screen. "file" DataChannel, one file at a time:
///   {"t":"file",name,size} → binary chunks → {"t":"end"}   ⇒ reply {"t":"saved",name} | {"t":"error",msg}
/// Saved to this PC's Desktop with a unique name; directories in the name are stripped. The dropped files are also put
/// on the clipboard as a cut, so Ctrl+V in any folder moves them there (simple stand-in for "drop onto that folder").
static class FileDrop
{
    public static void Attach(RTCDataChannel dc, MainForm ui)
    {
        FileStream? fs = null;
        string path = "";
        long size = 0;
        var batch = new List<string>(); // files of the current drop ({"t":"batch"} starts a new one)
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
                    case "batch": batch.Clear(); break;
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
                        // Also on the clipboard as "cut": Ctrl+V in any Explorer folder moves the drop there; otherwise it stays on the Desktop.
                        batch.Add(path);
                        Clip.SetFiles(batch);
                        ui.Notify("파일을 받았습니다", Path.GetFileName(path) + " (바탕화면)\n다른 폴더에서 Ctrl+V 하면 그 폴더로 옮겨집니다");
                        Reply(new { t = "saved", name = Path.GetFileName(path) });
                        path = "";
                        break;
                }
            }
            catch (Exception e) { Abort(); Reply(new { t = "error", msg = e.Message }); }
        };
        dc.onclose += Abort; // session ended mid-file: don't leave a partial file behind
    }

    /// This PC → viewer, same framing in the other direction: {"t":"file",name,size} → chunks → {"t":"end"}.
    /// The viewer saves it as a browser download.
    public static async Task Send(RTCDataChannel dc, string path)
    {
        await using var fs = File.OpenRead(path);
        dc.send(JsonSerializer.Serialize(new { t = "file", name = Path.GetFileName(path), size = fs.Length }));
        var buf = new byte[16384];
        int n;
        while ((n = await fs.ReadAsync(buf)) > 0)
        {
            while (dc.bufferedAmount > 1024 * 1024) // don't queue the whole file in memory
            {
                if (dc.readyState != RTCDataChannelState.open) throw new Exception("연결이 끊겼습니다");
                await Task.Delay(10);
            }
            dc.send(n == buf.Length ? buf : buf[..n]);
        }
        dc.send(JsonSerializer.Serialize(new { t = "end" }));
        Log.Write($"파일 보냄: {path} ({fs.Length / 1024} KB)");
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
