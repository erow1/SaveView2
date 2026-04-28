namespace SafeView.Web.Helpers;

/// <summary>
/// Single source of truth dla palety kolorów bbox-ów w UI. 8-kolorowa rotacja
/// dobrana pod ciemne tło — każdy kolor ma dobry kontrast i odróżnia się od sąsiadów
/// żeby przy gęstej scenie wykrywania stos szybko-skanować.
/// Reużywana w: ModelTestDialog, IncidentDetail, monitor.js (po stronie JS sama tabela).
/// </summary>
public static class BboxPalette
{
    /// <summary>Hex (#RRGGBB) z paletyo deterministycznie po indeksie detekcji.</summary>
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
}
