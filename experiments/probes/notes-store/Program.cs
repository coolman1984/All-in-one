// Can the inbox and notes live in the SQLite that ships inside Windows, with no package?
// Made-up pages only. Prints timings and sizes. Usage: exp-notes-store <db path> <pages>
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

static class Sql
{
    const string D = "winsqlite3.dll";
    [DllImport(D)] public static extern int sqlite3_open_v2(byte[] name, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(D)] public static extern int sqlite3_close(IntPtr db);
    [DllImport(D)] public static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr cb, IntPtr arg, IntPtr err);
    [DllImport(D)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr st, IntPtr tail);
    [DllImport(D)] public static extern int sqlite3_step(IntPtr st);
    [DllImport(D)] public static extern int sqlite3_reset(IntPtr st);
    [DllImport(D)] public static extern int sqlite3_finalize(IntPtr st);
    [DllImport(D)] public static extern int sqlite3_bind_text(IntPtr st, int i, byte[] s, int n, IntPtr destructor);
    [DllImport(D)] public static extern int sqlite3_column_int(IntPtr st, int i);
    [DllImport(D)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
    public static byte[] U(string s) => Encoding.UTF8.GetBytes(s + "\0");
}

static class Program
{
    static IntPtr db;
    static readonly IntPtr Transient = new(-1);

    static void Exec(string s)
    {
        if (Sql.sqlite3_exec(db, Sql.U(s), 0, 0, 0) != 0) throw new Exception(Marshal.PtrToStringUTF8(Sql.sqlite3_errmsg(db)));
    }

    // The search copy only. The page itself keeps the exact text the person wrote.
    public static string Fold(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c >= 'ً' && c <= 'ْ' || c == 'ٰ' || c == 'ـ') continue; // short vowels, dagger alif, tatweel
            b.Append(c switch { 'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', 'ى' => 'ي', 'ة' => 'ه', 'ؤ' => 'و', 'ئ' => 'ي', _ => char.ToLowerInvariant(c) });
        }
        return b.ToString();
    }

    static int Count(string query)
    {
        Sql.sqlite3_prepare_v2(db, Sql.U("select count(*) from pages_fts where pages_fts match ?1"), -1, out IntPtr st, 0);
        byte[] q = Encoding.UTF8.GetBytes(Fold(query));
        Sql.sqlite3_bind_text(st, 1, q, q.Length, Transient);
        Sql.sqlite3_step(st); int n = Sql.sqlite3_column_int(st, 0); Sql.sqlite3_finalize(st); return n;
    }

    static int Main(string[] a)
    {
        string path = a[0]; int pages = int.Parse(a[1]);
        if (!Path.GetFileName(path).StartsWith("syn", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("REFUSED: made-up store must be named syn*"); return 2; }
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) File.Delete(f);
        var sw = Stopwatch.StartNew();
        if (Sql.sqlite3_open_v2(Sql.U(path), out db, 6, 0) != 0) { Console.WriteLine("open failed"); return 1; }
        Exec("pragma journal_mode=wal; pragma synchronous=normal;");
        Exec("create table pages(id integer primary key, made text, kind text, body text);");
        Exec("create virtual table pages_fts using fts5(body, tokenize='trigram', content='', contentless_delete=1);");
        Console.WriteLine($"open and create: {sw.Elapsed.TotalMilliseconds:F0} ms");

        string[] words = { "تقرير الإنتاج الشهري", "مُراجعة المخزون", "فاتورة رقم 7741", "اجتماع إدارة المصنع", "شحنة مَواد خام", "Monthly stock review", "خطة الصيانة الوقائية", "ملاحظة من المهندس" };
        Sql.sqlite3_prepare_v2(db, Sql.U("insert into pages(id,made,kind,body) values(?1,'2026-11-04','text',?2)"), -1, out IntPtr ins, 0);
        Sql.sqlite3_prepare_v2(db, Sql.U("insert into pages_fts(rowid,body) values(?1,?2)"), -1, out IntPtr fts, 0);
        var rng = new Random(5); sw.Restart(); Exec("begin");
        for (int i = 1; i <= pages; i++)
        {
            string body = string.Join(" - ", words[rng.Next(8)], words[rng.Next(8)], "صفحة " + i);
            byte[] id = Encoding.UTF8.GetBytes(i.ToString()), b = Encoding.UTF8.GetBytes(body), s = Encoding.UTF8.GetBytes(Fold(body));
            Sql.sqlite3_bind_text(ins, 1, id, id.Length, Transient); Sql.sqlite3_bind_text(ins, 2, b, b.Length, Transient); Sql.sqlite3_step(ins); Sql.sqlite3_reset(ins);
            Sql.sqlite3_bind_text(fts, 1, id, id.Length, Transient); Sql.sqlite3_bind_text(fts, 2, s, s.Length, Transient); Sql.sqlite3_step(fts); Sql.sqlite3_reset(fts);
        }
        Exec("commit"); Sql.sqlite3_finalize(ins); Sql.sqlite3_finalize(fts);
        Console.WriteLine($"insert {pages} pages: {sw.Elapsed.TotalSeconds:F2} s ({pages / sw.Elapsed.TotalSeconds:F0} per second)");

        // One saved page, the way the inbox would save it: its own transaction, safely on disk before saying "saved".
        var t = new List<double>();
        for (int i = 0; i < 50; i++) { sw.Restart(); Exec($"insert into pages(made,kind,body) values('2026-11-04','text','صفحة وارد {i}')"); t.Add(sw.Elapsed.TotalMilliseconds); }
        t.Sort(); Console.WriteLine($"save one page: median {t[25]:F2} ms, worst {t[49]:F2} ms");

        foreach (var q in new[] { "مراجعة المخزون", "مُراجعة المخزون", "فاتوره", "راجعة", "stock review", "لا يوجد هذا النص" })
        {
            sw.Restart(); int n = Count(q); Console.WriteLine($"search [{q}]: {n} pages in {sw.Elapsed.TotalMilliseconds:F1} ms");
        }
        Sql.sqlite3_close(db);
        Console.WriteLine($"file: {new FileInfo(path).Length / 1048576.0:F1} MB; program memory {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MB");
        return 0;
    }
}
