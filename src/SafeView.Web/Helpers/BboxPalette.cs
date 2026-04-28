using System.Globalization;

namespace SafeView.Web.Helpers;

/// <summary>
/// Single source of truth dla palety kolorów bbox-ów w UI. 8-kolorowa rotacja
/// dobrana pod ciemne tło — każdy kolor ma dobry kontrast i odróżnia się od sąsiadów
/// żeby przy gęstej scenie wykrywania stos szybko-skanować.
/// Reużywana w: ModelTestDialog, IncidentDetail, monitor.js (po stronie JS sama tabela).
/// </summary>
public static class BboxPalette
{
    /// <summary>Hex (#RRGGBB) z palety deterministycznie po indeksie detekcji.</summary>
    public static readonly string[] Colors =
    [
        "#4DA6FF", // niebieski
        "#2EC27E", // zielony
        "#F5A524", // pomarańczowy
        "#FF5964", // czerwony
        "#A855F7", // fioletowy
        "#EC4899", // róż
        "#14B8A6", // teal
        "#EAB308"  // żółty
    ];

    public static string ColorAt(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];

    /// <summary>
    /// Hex (#RRGGBB) → rgba(r,g,b,a) z InvariantCulture (kropki). Używane do generowania
    /// tła etykiety w inline-style — niezależne od CSS color-mix i lokalizacji.
    /// </summary>
    public static string Rgba(string hex, double alpha)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6) return hex;
        try
        {
            var r = Convert.ToInt32(h[..2], 16);
            var g = Convert.ToInt32(h[2..4], 16);
            var b = Convert.ToInt32(h[4..6], 16);
            var a = alpha.ToString("F2", CultureInfo.InvariantCulture);
            return $"rgba({r},{g},{b},{a})";
        }
        catch { return hex; }
    }
}
