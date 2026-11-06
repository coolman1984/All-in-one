// Stack: Windows Forms. Two grids: the stock virtual DataGridView (what the Excel viewer uses today), and with --canvas a small
// hand-drawn grid, which is the fair match for the hand-drawn canvas in the WebView2 stack.
// The same source builds on the .NET 8 SDK and on the .NET Framework 4.8 compiler, so it is kept to old C#.
// Driven by the watcher through stdin; prints only timings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Draws only what is on screen into one bitmap. No cell objects, no per-cell events.
sealed class FastGrid : Control
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, int rop);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr dc, int m);
    [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr dc, int c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr dc, string s, int n, ref RECT r, uint f);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    const int WHITENESS = 0x00FF0062, SRCCOPY = 0x00CC0020;
    const uint SINGLELINE = 0x20, VCENTER = 4, NOPREFIX = 0x800, RIGHT = 2, RTLREADING = 0x20000;

    const int RH = 22, Head = 24, CW = 110, RowHead = 70;
    IntPtr mem, bmp; int bw, bh; public bool On, Rtl; int first;
    readonly Font font = new Font("Segoe UI", 9f); IntPtr hfont;

    public FastGrid() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true); hfont = font.ToHfont(); }

    void Cell(string s, int x, int y, int w)
    {
        var r = new RECT { l = x, t = y, r = x + w, b = y + RH };
        DrawTextW(mem, s, s.Length, ref r, SINGLELINE | VCENTER | NOPREFIX | (Rtl ? RIGHT | RTLREADING : 0));
    }

    void Surface(int w, int h)
    {
        if (mem != IntPtr.Zero && bw == w && bh == h) return;
        if (mem != IntPtr.Zero) { DeleteDC(mem); DeleteObject(bmp); }
        IntPtr screen = GetDC(IntPtr.Zero); mem = CreateCompatibleDC(screen); bmp = CreateCompatibleBitmap(screen, w, h); ReleaseDC(IntPtr.Zero, screen);
        SelectObject(mem, bmp); SelectObject(mem, hfont); SetBkMode(mem, 1); bw = w; bh = h;
    }

    public void Frame(int firstRow)
    {
        first = firstRow; int w = Math.Max(1, Width), h = Math.Max(1, Height);
        Surface(w, h);
        PatBlt(mem, 0, 0, w, h, WHITENESS);
        if (!On) return;
        int rows = Math.Min(Synth.Rows - first, (h - Head) / RH + 1), cols = Math.Min(Synth.Cols, (w - RowHead) / CW + 1);
        SetTextColor(mem, 0x505050);
        for (int c = 0; c < cols; c++) Cell(Synth.Title(c), Rtl ? w - RowHead - (c + 1) * CW : RowHead + c * CW + 6, 0, CW - 6);
        for (int i = 0; i < rows; i++)
        {
            int y = Head + i * RH;
            SetTextColor(mem, 0x808080); Cell((first + i + 1).ToString(), Rtl ? w - RowHead : 6, y, RowHead - 6);
            SetTextColor(mem, 0);
            for (int c = 0; c < cols; c++) Cell(Synth.Cell(first + i, c), Rtl ? w - RowHead - (c + 1) * CW : RowHead + c * CW + 6, y, CW - 6);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (mem == IntPtr.Zero) Frame(first);
        IntPtr d = e.Graphics.GetHdc(); BitBlt(d, 0, 0, bw, bh, mem, 0, 0, SRCCOPY); e.Graphics.ReleaseHdc(d);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Frame(first); Invalidate(); }
}

static class Program
{
    static Form form; static DataGridView grid; static FastGrid fast; static Bitmap shot;

    static void Say(string s) { Console.Out.WriteLine(s); Console.Out.Flush(); }

    static void Draw()
    {
        if (fast != null) return; // Frame already drew everything into memory
        if (shot == null || shot.Width != grid.Width || shot.Height != grid.Height) shot = new Bitmap(Math.Max(1, grid.Width), Math.Max(1, grid.Height));
        grid.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
    }

    static void Go(int row) { if (fast != null) fast.Frame(row); else grid.FirstDisplayedScrollingRowIndex = row; Draw(); }

    static void Table()
    {
        var sw = Stopwatch.StartNew();
        if (fast != null) { fast.On = true; fast.Frame(0); }
        else
        {
            grid.SuspendLayout();
            var cols = new DataGridViewColumn[Synth.Cols];
            for (int k = 0; k < Synth.Cols; k++) cols[k] = new DataGridViewTextBoxColumn { HeaderText = Synth.Title(k), Width = 110, SortMode = DataGridViewColumnSortMode.Programmatic };
            grid.Columns.AddRange(cols);
            grid.RowCount = Synth.Rows;
            grid.ResumeLayout();
            Draw();
        }
        Say("TABLE_READY " + sw.Elapsed.TotalMilliseconds.ToString("F1"));
    }

    static double Pick(List<double> t, int i) { return t[Math.Min(t.Count - 1, i)]; }

    static void Bench()
    {
        var rng = new Random(3); var t = new List<double>(); var sw = new Stopwatch();
        for (int i = 0; i < 200; i++) { sw.Restart(); Go(rng.Next(0, Synth.Rows - 40)); t.Add(sw.Elapsed.TotalMilliseconds); }
        t.Sort(); string jump = "jump_med=" + Pick(t, 100).ToString("F2") + " jump_p95=" + Pick(t, 190).ToString("F2");
        t.Clear(); int row = 0;
        for (int i = 0; i < 200; i++) { sw.Restart(); row += 3; Go(row); t.Add(sw.Elapsed.TotalMilliseconds); }
        t.Sort(); Say("BENCH " + jump + " wheel_med=" + Pick(t, 100).ToString("F2") + " wheel_p95=" + Pick(t, 190).ToString("F2"));
    }

    static void Rtl(string png)
    {
        form.RightToLeft = RightToLeft.Yes; form.RightToLeftLayout = true;
        if (fast != null) { fast.Rtl = true; fast.Frame(0); } else { grid.RightToLeft = RightToLeft.Yes; grid.FirstDisplayedScrollingRowIndex = 0; }
        using (var b = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); b.Save(png); }
        Say("RTL_DONE");
    }

    static void Handle(string line)
    {
        if (line == "table") Table();
        else if (line == "bench") Bench();
        else if (line.StartsWith("rtl ")) Rtl(line.Substring(4));
        else if (line == "quit") form.Close();
    }

    [STAThread]
    static int Main(string[] a)
    {
        bool canvas = a.Length > 0 && a[0] == "--canvas";
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        form = new Form { Text = "experiment", StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(1280, 800), ShowInTaskbar = false, Font = new Font("Segoe UI", 9f) };
        var bar = new ToolStrip(); bar.Items.Add("ملف"); bar.Items.Add("بحث");
        var side = new ListBox { Dock = DockStyle.Left, Width = 180 }; side.Items.AddRange(new object[] { "الوارد", "الجداول", "الملاحظات" });
        if (canvas) { fast = new FastGrid { Dock = DockStyle.Fill }; form.Controls.Add(fast); }
        else
        {
            grid = new DataGridView { Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false, AllowUserToResizeRows = false, RowHeadersWidth = 70, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None };
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; grid.RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.RowTemplate.Height = 22;
            typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(grid, true, null);
            grid.CellValueNeeded += (s, e) => { e.Value = Synth.Cell(e.RowIndex, e.ColumnIndex); };
            form.Controls.Add(grid);
        }
        form.Controls.Add(side); form.Controls.Add(bar);
        form.Shown += (s, e) =>
        {
            Draw(); Say("READY");
            var th = new Thread(() =>
            {
                string line;
                while ((line = Console.In.ReadLine()) != null) { string l = line; form.BeginInvoke((Action)(() => Handle(l))); }
                form.BeginInvoke((Action)(() => form.Close())); // the watcher is gone
            }); th.IsBackground = true; th.Start();
        };
        Application.Run(form);
        return 0;
    }
}
