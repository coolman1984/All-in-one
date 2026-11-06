// Stack: a native window whose whole interface is HTML/CSS/JS in WebView2. Rows come from C# one screen at a time.
// Arguments: --udf <folder> [--lean]. Driven by the watcher through stdin.
using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    static Form form; static WebView2 web;
    static void Say(string s) { Console.Out.WriteLine(s); Console.Out.Flush(); }

    public static string RowsJson(int id, int from, int n)
    {
        using var ms = new MemoryStream(); using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject(); w.WriteString("t", "rows"); w.WriteNumber("id", id); w.WriteStartArray("rows");
            for (int r = from; r < Math.Min(Synth.Rows, from + n); r++) { w.WriteStartArray(); for (int c = 0; c < Synth.Cols; c++) w.WriteStringValue(Synth.Cell(r, c)); w.WriteEndArray(); }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    static void OnMessage(object s, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var d = JsonDocument.Parse(e.WebMessageAsJson); var m = d.RootElement; string t = m.GetProperty("t").GetString();
        if (t == "rows") web.CoreWebView2.PostWebMessageAsJson(RowsJson(m.GetProperty("id").GetInt32(), m.GetProperty("from").GetInt32(), m.GetProperty("n").GetInt32()));
        else if (t == "say") Say(m.GetProperty("s").GetString());
    }

    static async Task Rtl(string png)
    {
        await web.CoreWebView2.ExecuteScriptAsync("setRtl()");
        await Task.Delay(300);
        using (var f = File.Create(png)) await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, f);
        Say("RTL_DONE");
    }

    static async void Handle(string line)
    {
        if (line == "table") await web.CoreWebView2.ExecuteScriptAsync("loadTable()");
        else if (line == "bench") await web.CoreWebView2.ExecuteScriptAsync("bench()");
        else if (line.StartsWith("rtl ")) await Rtl(line.Substring(4));
        else if (line == "quit") form.Close();
    }

    static async void Start(string udf, bool lean)
    {
        var opt = new CoreWebView2EnvironmentOptions();
        if (lean) opt.AdditionalBrowserArguments = "--disable-gpu --disable-extensions --disable-background-networking --disable-component-update --disable-sync";
        var env = await CoreWebView2Environment.CreateAsync(null, udf, opt);
        await web.EnsureCoreWebView2Async(env);
        var c = web.CoreWebView2;
        c.Settings.AreDevToolsEnabled = false; c.Settings.IsStatusBarEnabled = false; c.Settings.AreDefaultContextMenusEnabled = false;
        c.WebMessageReceived += OnMessage;
        c.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
        c.Navigate("https://app.local/grid.html");
    }

    [STAThread]
    static int Main(string[] a)
    {
        string udf = Path.Combine(Path.GetTempPath(), "exp-wv2"); bool lean = false;
        for (int i = 0; i < a.Length; i++) { if (a[i] == "--udf") udf = a[++i]; else if (a[i] == "--lean") lean = true; }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        form = new Form { Text = "experiment", StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(1280, 800), ShowInTaskbar = false };
        web = new WebView2 { Dock = DockStyle.Fill }; form.Controls.Add(web);
        form.Shown += (s, e) =>
        {
            Start(udf, lean);
            new Thread(() =>
            {
                string line;
                while ((line = Console.In.ReadLine()) != null) { string l = line; form.BeginInvoke((Action)(() => Handle(l))); }
                form.BeginInvoke((Action)(() => form.Close())); // the watcher is gone
            }) { IsBackground = true }.Start();
        };
        Application.Run(form);
        web.Dispose();
        return 0;
    }
}
