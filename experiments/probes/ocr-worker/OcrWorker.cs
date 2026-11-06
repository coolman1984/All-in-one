// An OCR expert built by the C# compiler that ships inside Windows, reaching Windows' own OCR with no package.
// Usage: exp-ocr.exe <picture> <language>. Made-up pictures only.
using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

static class Program
{
    static async Task<int> Run(string path, string lang)
    {
        var sw = Stopwatch.StartNew();
        var file = await StorageFile.GetFileFromPathAsync(path);
        using (var stream = await file.OpenAsync(FileAccessMode.Read))
        {
            var dec = await BitmapDecoder.CreateAsync(stream);
            var bmp = await dec.GetSoftwareBitmapAsync();
            var eng = OcrEngine.TryCreateFromLanguage(new Language(lang));
            var res = await eng.RecognizeAsync(bmp);
            Console.WriteLine("lines " + res.Lines.Count + " in " + sw.ElapsedMilliseconds + " ms");
            // Windows gives Arabic words left to right on the picture, so a right-to-left line must be turned around.
            foreach (var l in res.Lines)
            {
                var w = l.Words.Select(x => x.Text).ToList();
                if (w.Any(t => t.Any(c => c >= 0x0600 && c <= 0x06FF))) w.Reverse();
                Console.WriteLine("  " + string.Join(" ", w));
            }
        }
        Console.WriteLine("memory " + Process.GetCurrentProcess().PrivateMemorySize64 / 1048576 + " MB");
        return 0;
    }

    static int Main(string[] a)
    {
        Console.OutputEncoding = Encoding.UTF8;
        return Run(a[0], a[1]).GetAwaiter().GetResult();
    }
}
