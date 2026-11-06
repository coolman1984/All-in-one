// Made-up table used by every stack. Nothing here comes from a real file.
// Kept to old C# so the .NET Framework compiler that ships with Windows can build it too.
using System;
using System.Globalization;

public static class Synth
{
    public const int Rows = 1000000, Cols = 20;

    static readonly string[] Places = {
        "مخزن القاهرة", "Cairo Store", "مصنع ٦ أكتوبر", "Line B - خط ب", "الإسكندرية", "Plant 7", "مخزن رقم 12", "Giza Hub"
    };
    static readonly string[] Items = {
        "مسمار صلب 10 مم", "Steel bolt M8", "غطاء بلاستيك", "Cable 2.5 mm", "محرك صغير", "Valve A-17", "لوحة تحكم", "Bearing 6204"
    };

    public static string Title(int c) { return "C" + (c + 1); }

    /// <summary>The same text every time for the same cell, built on demand, so a stack holds no table in memory.</summary>
    public static string Cell(int r, int c)
    {
        unchecked
        {
            uint h = (uint)(r * 2654435761u) ^ (uint)(c * 40503u);
            h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
            switch (c % 5)
            {
                case 0: return (100000 + r).ToString(CultureInfo.InvariantCulture);
                case 1: return Places[h % 8];
                case 2: return Items[(h >> 3) % 8];
                case 3: return ((h % 10000000) / 100.0).ToString("N2", CultureInfo.InvariantCulture);
                default: return "2026-" + (1 + h % 12).ToString("00", CultureInfo.InvariantCulture) + "-" + (1 + (h >> 5) % 28).ToString("00", CultureInfo.InvariantCulture);
            }
        }
    }
}
