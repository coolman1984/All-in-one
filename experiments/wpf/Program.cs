// Stack: WPF with a virtualized DataGrid over a list that makes rows on demand. Driven by the watcher through stdin.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public sealed class Row
{
    readonly int r; public Row(int r) { this.r = r; }
    public string this[int c] => Synth.Cell(r, c);
}

// A list of a million rows that holds none of them.
sealed class VirtualRows : IList
{
    public int Count => Synth.Rows;
    public object this[int i] { get => new Row(i); set => throw new NotSupportedException(); }
    public bool IsFixedSize => true; public bool IsReadOnly => true; public bool IsSynchronized => false; public object SyncRoot => this;
    public int Add(object v) => throw new NotSupportedException(); public void Clear() => throw new NotSupportedException();
    public bool Contains(object v) => false; public int IndexOf(object v) => -1;
    public void Insert(int i, object v) => throw new NotSupportedException(); public void Remove(object v) => throw new NotSupportedException(); public void RemoveAt(int i) => throw new NotSupportedException();
    public void CopyTo(Array a, int i) => throw new NotSupportedException();
    public IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return new Row(i); }
}

static class Program
{
    static Window win; static DataGrid grid; static ScrollViewer sv; static FrameworkElement inner;
    static void Say(string s) { Console.Out.WriteLine(s); Console.Out.Flush(); }

    static T Find<T>(DependencyObject o) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) { var c = VisualTreeHelper.GetChild(o, i); if (c is T t) return t; var f = Find<T>(c); if (f != null) return f; }
        return null;
    }

    static void Flush() => win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

    static RenderTargetBitmap Draw(FrameworkElement e)
    {
        var b = new RenderTargetBitmap(Math.Max(1, (int)e.ActualWidth), Math.Max(1, (int)e.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        b.Render(e); return b;
    }

    static void Table()
    {
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < Synth.Cols; k++) grid.Columns.Add(new DataGridTextColumn { Header = Synth.Title(k), Width = 110, Binding = new Binding("[" + k + "]") { Mode = BindingMode.OneTime } });
        grid.ItemsSource = new VirtualRows();
        grid.UpdateLayout(); Flush(); sv = Find<ScrollViewer>(grid); Draw(grid);
        Say("TABLE_READY " + sw.Elapsed.TotalMilliseconds.ToString("F1"));
    }

    static void Bench()
    {
        var rng = new Random(3); var t = new List<double>(); var sw = new Stopwatch();
        for (int i = 0; i < 200; i++) { sw.Restart(); sv.ScrollToVerticalOffset(rng.Next(0, Synth.Rows - 40)); grid.UpdateLayout(); Flush(); Draw(grid); t.Add(sw.Elapsed.TotalMilliseconds); }
        t.Sort(); string jump = $"jump_med={t[100]:F2} jump_p95={t[190]:F2}";
        t.Clear(); int row = 0;
        for (int i = 0; i < 200; i++) { sw.Restart(); row += 3; sv.ScrollToVerticalOffset(row); grid.UpdateLayout(); Flush(); Draw(grid); t.Add(sw.Elapsed.TotalMilliseconds); }
        t.Sort(); Say($"BENCH {jump} wheel_med={t[100]:F2} wheel_p95={t[190]:F2}");
    }

    static void Rtl(string png)
    {
        // Set on the inner panel, and the outer one is captured: capturing an element that is itself mirrored gives a mirrored picture.
        inner.FlowDirection = FlowDirection.RightToLeft; sv.ScrollToVerticalOffset(0); grid.UpdateLayout(); Flush();
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(Draw((FrameworkElement)win.Content)));
        using (var f = File.Create(png)) enc.Save(f);
        Say("RTL_DONE");
    }

    static void Handle(string line)
    {
        if (line == "table") Table();
        else if (line == "bench") Bench();
        else if (line.StartsWith("rtl ")) Rtl(line.Substring(4));
        else if (line == "quit") win.Close();
    }

    [STAThread]
    static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var dock = new DockPanel { LastChildFill = true };
        var bar = new ToolBar(); bar.Items.Add(new Button { Content = "ملف" }); bar.Items.Add(new Button { Content = "بحث" }); DockPanel.SetDock(bar, Dock.Top);
        var side = new ListBox { Width = 180 }; side.Items.Add("الوارد"); side.Items.Add("الجداول"); side.Items.Add("الملاحظات"); DockPanel.SetDock(side, Dock.Left);
        grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, RowHeight = 22 };
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(grid, ScrollUnit.Item);
        dock.Children.Add(bar); dock.Children.Add(side); dock.Children.Add(grid);
        inner = dock;
        win = new Window { Title = "experiment", WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000, Width = 1280, Height = 800, ShowInTaskbar = false, ShowActivated = false, Content = new Border { Child = dock }, FontFamily = new FontFamily("Segoe UI") };
        win.ContentRendered += (s, e) =>
        {
            Say("READY");
            new Thread(() =>
            {
                string line;
                while ((line = Console.In.ReadLine()) != null) { string l = line; win.Dispatcher.BeginInvoke((Action)(() => Handle(l))); }
                win.Dispatcher.BeginInvoke((Action)(() => win.Close())); // the watcher is gone
            }) { IsBackground = true }.Start();
        };
        app.Run(win);
        return 0;
    }
}
