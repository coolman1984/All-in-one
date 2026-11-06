// Stack: a small native core, with each heavy part started only on request and closed after.
// Commands come from the watcher through stdin. Prints only timings. Made-up data only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExcelViewer;
using ExcelViewer.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Win32
{
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr h, int i, uint v);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    public const int GWL_STYLE = -16; public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
}

sealed class PluginContext : AssemblyLoadContext
{
    public PluginContext() : base("module", isCollectible: true) { }
    protected override Assembly Load(AssemblyName n) => null; // shared parts come from the app
}

static class Program
{
    static Form form; static Panel host; static Bitmap shot;
    static readonly Stopwatch clock = new();
    static void Say(string s) { Console.Out.WriteLine(s); Console.Out.Flush(); }

    // --- native table ---
    static DataGridView grid;
    static void GridOpen()
    {
        clock.Restart();
        grid = new DataGridView { Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false, RowHeadersWidth = 70, BorderStyle = BorderStyle.None, BackgroundColor = SystemColors.Window };
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; grid.RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.RowTemplate.Height = 22;
        typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(grid, true);
        grid.CellValueNeeded += (s, e) => e.Value = Synth.Cell(e.RowIndex, e.ColumnIndex);
        var cols = new DataGridViewColumn[Synth.Cols];
        for (int k = 0; k < Synth.Cols; k++) cols[k] = new DataGridViewTextBoxColumn { HeaderText = Synth.Title(k), Width = 110, SortMode = DataGridViewColumnSortMode.Programmatic };
        grid.Columns.AddRange(cols); grid.RowCount = Synth.Rows; host.Controls.Add(grid);
        Draw(grid); Say($"GRID_READY {clock.Elapsed.TotalMilliseconds:F1}");
    }
    static void Draw(Control c)
    {
        if (shot == null || shot.Width != c.Width || shot.Height != c.Height) shot = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
        c.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
    }
    static void GridClose() { host.Controls.Remove(grid); grid.Dispose(); grid = null; shot?.Dispose(); shot = null; GC.Collect(); GC.WaitForPendingFinalizers(); Say("GRID_CLOSED"); }

    // --- rich module: WebView2 starts here, not at program start ---
    static WebView2 web; static CoreWebView2Environment env; static string udf; static bool lean;
    static async void WebOpen()
    {
        clock.Restart();
        var opt = new CoreWebView2EnvironmentOptions();
        if (lean) opt.AdditionalBrowserArguments = "--disable-gpu --disable-extensions --disable-background-networking --disable-component-update --disable-sync";
        env = await CoreWebView2Environment.CreateAsync(null, udf, opt);
        web = new WebView2 { Dock = DockStyle.Fill }; host.Controls.Add(web); web.BringToFront();
        await web.EnsureCoreWebView2Async(env);
        var c = web.CoreWebView2; c.Settings.AreDevToolsEnabled = false;
        c.WebMessageReceived += (s, e) =>
        {
            string m = e.WebMessageAsJson;
            if (m.Contains("\"say\"") && m.Contains("READY")) Say($"WEB_READY {clock.Elapsed.TotalMilliseconds:F1}");
        };
        c.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
        c.Navigate("https://app.local/grid.html");
    }
    static void WebLow() { web.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; Say("WEB_LOW"); }
    static async void WebSuspend() { web.Visible = false; bool ok = await web.CoreWebView2.TrySuspendAsync(); Say("WEB_SUSPENDED " + ok); }
    static void WebResume() { clock.Restart(); web.Visible = true; web.CoreWebView2.Resume(); Say($"WEB_RESUMED {clock.Elapsed.TotalMilliseconds:F1}"); }
    static void WebClose() { host.Controls.Remove(web); web.Dispose(); web = null; env = null; GC.Collect(); GC.WaitForPendingFinalizers(); Say("WEB_CLOSED"); }

    // --- worker: a separate process with its own short life ---
    static Process worker;
    static void WorkerOpen()
    {
        clock.Restart();
        worker = Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--worker") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true });
        worker.StandardInput.WriteLine("ping"); worker.StandardInput.Flush();
        string a = worker.StandardOutput.ReadLine();
        Say($"WORKER_READY {clock.Elapsed.TotalMilliseconds:F1} {a}");
    }
    static void WorkerClose() { worker.StandardInput.Close(); worker.WaitForExit(5000); worker.Dispose(); worker = null; Say("WORKER_CLOSED"); }
    static int WorkerMain() { string l; while ((l = Console.In.ReadLine()) != null) { Console.Out.WriteLine("pong"); Console.Out.Flush(); } return 0; }

    // --- module: an assembly loaded on demand into a context that can be unloaded ---
    static WeakReference pluginRef; static Control pluginView; static object pluginObj; static PluginContext plugin;
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PluginOpen()
    {
        clock.Restart();
        plugin = new PluginContext();
        var asm = plugin.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "modules", "module-sample.dll"));
        pluginObj = Activator.CreateInstance(asm.GetType("ModuleSample.Module"));
        pluginView = (Control)pluginObj.GetType().GetMethod("Build").Invoke(pluginObj, null);
        host.Controls.Add(pluginView); pluginRef = new WeakReference(plugin);
        Say($"PLUGIN_READY {clock.Elapsed.TotalMilliseconds:F1}");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PluginDrop() { host.Controls.Remove(pluginView); pluginView.Dispose(); pluginView = null; pluginObj = null; plugin.Unload(); plugin = null; }
    static void PluginClose()
    {
        PluginDrop();
        for (int i = 0; i < 10 && pluginRef.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Say("PLUGIN_CLOSED unloaded=" + !pluginRef.IsAlive);
    }

    // --- the real Excel viewer window, inside this process, loaded the first time it is used ---
    static Form viewer;
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ViewerOpen(string xvc)
    {
        clock.Restart();
        var f = new MainForm { Unattended = true, TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };
        host.Controls.Add(f); f.Show(); f.BringToFront();
        f.Source(Path.Combine(Path.GetDirectoryName(xvc), "syn-demo.xlsx")); f.LoadForTest(xvc);
        Draw(f.Grid); viewer = f;
        Say($"VIEWER_READY {clock.Elapsed.TotalMilliseconds:F1} rows={f.Grid.RowCount}");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ViewerBench()
    {
        var f = (MainForm)viewer; var g = f.Grid; var rng = new Random(3); var t = new List<double>(); var sw = new Stopwatch();
        for (int i = 0; i < 200; i++) { sw.Restart(); g.FirstDisplayedScrollingRowIndex = rng.Next(0, Math.Max(1, g.RowCount - 40)); Draw(g); t.Add(sw.Elapsed.TotalMilliseconds); }
        t.Sort(); Say($"VIEWER_BENCH jump_med={t[100]:F2} jump_p95={t[190]:F2}");
    }
    static void ViewerClose() { host.Controls.Remove(viewer); viewer.Close(); viewer.Dispose(); viewer = null; shot?.Dispose(); shot = null; GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Say("VIEWER_CLOSED"); }

    // --- the same viewer in its own process, its window placed inside ours ---
    static Process viewerProc;
    static void ViewerProcOpen(string xvc)
    {
        clock.Restart();
        var psi = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add("--viewer-child"); psi.ArgumentList.Add(host.Handle.ToInt64().ToString()); psi.ArgumentList.Add(xvc); psi.ArgumentList.Add(host.Width.ToString()); psi.ArgumentList.Add(host.Height.ToString());
        viewerProc = Process.Start(psi);
        Task.Run(() => { string a = viewerProc.StandardOutput.ReadLine(); form.BeginInvoke(() => Say($"VIEWERPROC_READY {clock.Elapsed.TotalMilliseconds:F1} {a}")); });
    }
    static void ViewerProcClose() { viewerProc.StandardInput.Close(); bool ok = viewerProc.WaitForExit(8000); viewerProc.Dispose(); viewerProc = null; Say("VIEWERPROC_CLOSED clean=" + ok); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ViewerChild(long parent, string xvc, int w, int h)
    {
        ApplicationConfiguration_Init();
        var f = new MainForm { Unattended = true, FormBorderStyle = FormBorderStyle.None }; f.PlaceOffScreen();
        f.Shown += (s, e) =>
        {
            f.Source(Path.Combine(Path.GetDirectoryName(xvc), "syn-demo.xlsx")); f.LoadForTest(xvc);
            Win32.SetWindowLong(f.Handle, Win32.GWL_STYLE, Win32.WS_CHILD | Win32.WS_VISIBLE);
            IntPtr old = Win32.SetParent(f.Handle, new IntPtr(parent)); int err = Marshal.GetLastWin32Error();
            Win32.MoveWindow(f.Handle, 0, 0, w, h, true);
            Console.Out.WriteLine("placed=" + (err == 0)); Console.Out.Flush();
            new Thread(() => { try { while (Console.In.ReadLine() != null) { } } catch (IOException) { } f.BeginInvoke(f.Close); }) { IsBackground = true }.Start();
        };
        Application.Run(f);
        return 0;
    }

    static void ApplicationConfiguration_Init() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); }

    // --- a made-up fast copy, written next to the experiment, never to the viewer's own folder ---
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int MakeXvc(string path, int rows)
    {
        if (!Path.GetFileName(path).StartsWith("syn", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("REFUSED: made-up copies must be named syn*"); return 2; }
        if (Path.GetFullPath(path).StartsWith(CachePolicy.Folder, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("REFUSED: not in the viewer's folder"); return 2; }
        const int block = 1000; int cols = Synth.Cols;
        var d = new SheetInfo { Name = "Sheet1", FirstRow = 1, FirstCol = 1, Rows = rows, Cols = cols, BlockRows = block, ColWidths = new float[cols], RunStart = new int[cols][], RunFmt = new int[cols][] };
        for (int k = 0; k < cols; k++) { d.ColWidths[k] = 14f; d.RunStart[k] = new[] { 0 }; d.RunFmt[k] = new[] { 0 }; }
        var c = new CacheData(); c.Formats.Add(new FormatEntry { Code = "General", Status = FormatStatus.MatchesExcel, Samples = 1 }); c.Sheets.Add(d);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write); using var w = new BinaryWriter(fs, Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes(CacheData.Magic)); w.Write(0L); w.Write(0L);
        for (int r = 0; r < rows; r += block)
        {
            int n = Math.Min(block, rows - r); var off = new long[cols + 1];
            for (int k = 0; k < cols; k++)
            {
                var kinds = new byte[n]; double[] nums = null; string[] text = null; bool isNum = k % 5 == 0;
                if (isNum) nums = new double[n]; else text = new string[n];
                for (int i = 0; i < n; i++) { if (isNum) { kinds[i] = CellKind.Number; nums[i] = 100000 + r + i; } else { kinds[i] = CellKind.Text; text[i] = Synth.Cell(r + i, k); } }
                var cc = ColumnChunk.Build(n, kinds, nums, text); d.Hash = cc.Mix(d.Hash, n);
                w.Flush(); off[k] = fs.Position; cc.Write(w);
            }
            w.Flush(); off[cols] = fs.Position; d.Chunks.Add(off);
        }
        w.Flush(); long at = fs.Position; c.WriteDirectory(w); w.Write(at);
        Console.WriteLine($"MADE {new FileInfo(path).Length / 1048576.0:F1} MB"); return 0;
    }

    static void Handle(string line)
    {
        try
        {
            string[] p = line.Split(' ', 2);
            switch (p[0])
            {
                case "grid": GridOpen(); break;
                case "gridclose": GridClose(); break;
                case "web": WebOpen(); break;
                case "weblow": WebLow(); break;
                case "websuspend": WebSuspend(); break;
                case "webresume": WebResume(); break;
                case "webclose": WebClose(); break;
                case "worker": WorkerOpen(); break;
                case "workerclose": WorkerClose(); break;
                case "plugin": PluginOpen(); break;
                case "pluginclose": PluginClose(); break;
                case "viewer": ViewerOpen(p[1]); break;
                case "viewerbench": ViewerBench(); break;
                case "viewerclose": ViewerClose(); break;
                case "viewerproc": ViewerProcOpen(p[1]); break;
                case "viewerprocclose": ViewerProcClose(); break;
                case "quit": form.Close(); break;
            }
        }
        catch (Exception e) { Say("ERROR " + p0(line) + " " + e.GetType().Name + " " + e.Message); }
    }
    static string p0(string l) => l.Split(' ')[0];

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length >= 1 && a[0] == "--worker") return WorkerMain();
        if (a.Length >= 3 && a[0] == "--make-xvc") return MakeXvc(a[1], int.Parse(a[2]));
        if (a.Length >= 5 && a[0] == "--viewer-child") return ViewerChild(long.Parse(a[1]), a[2], int.Parse(a[3]), int.Parse(a[4]));
        udf = Path.Combine(Path.GetTempPath(), "exp-hybrid");
        for (int i = 0; i < a.Length; i++) { if (a[i] == "--udf") udf = a[++i]; else if (a[i] == "--lean") lean = true; }
        ApplicationConfiguration_Init();
        form = new Form { Text = "experiment", StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(1280, 800), ShowInTaskbar = false, Font = new Font("Segoe UI", 9f) };
        var bar = new ToolStrip(); bar.Items.Add("ملف"); bar.Items.Add("بحث");
        var side = new ListBox { Dock = DockStyle.Left, Width = 180 }; side.Items.AddRange(new object[] { "الوارد", "الجداول", "الملاحظات" });
        host = new Panel { Dock = DockStyle.Fill };
        form.Controls.Add(host); form.Controls.Add(side); form.Controls.Add(bar);
        form.Shown += (s, e) =>
        {
            Draw(form); Say("READY");
            new Thread(() =>
            {
                string line;
                while ((line = Console.In.ReadLine()) != null) { string l = line; form.BeginInvoke(() => Handle(l)); }
                form.BeginInvoke(() => form.Close()); // the watcher is gone
            }) { IsBackground = true }.Start();
        };
        Application.Run(form);
        return 0;
    }
}
