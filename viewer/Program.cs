using System.Diagnostics;
using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HtUtil.Viewer;

// The web viewer/admin page in a native window (WebView2 = Edge engine, built into Windows 10/11).
// Benefits over a browser tab: Ctrl+W/F5/Ctrl+F etc. reach the remote PC, permissions are pre-granted.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var url = args.FirstOrDefault() // HtUtil.Viewer.exe http://localhost:5000 for testing
            ?? typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "Url").Value!;
        var host = new Uri(url).Host;

        var form = new Form { Text = "HtUtil", Width = 1280, Height = 800, Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) };
        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);
        form.Load += async (_, _) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HtUtil", "Viewer");
                await web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, dir));
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show("WebView2 런타임이 없습니다. 설치 페이지를 엽니다.", "HtUtil");
                Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
                form.Close();
                return;
            }
            var cw = web.CoreWebView2;
            cw.Settings.AreBrowserAcceleratorKeysEnabled = false; // shortcuts go to the page (remote PC)
            cw.PermissionRequested += (_, e) => // clipboard-read, window-management (multi-monitor) — only for our site
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Host == host) e.State = CoreWebView2PermissionState.Allow;
            };
            cw.DocumentTitleChanged += (_, _) => form.Text = cw.DocumentTitle;
            cw.Navigate(url);
        };
        Application.Run(form);
    }
}
