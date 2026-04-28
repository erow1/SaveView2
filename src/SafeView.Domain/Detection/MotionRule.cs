namespace SafeView.Domain.Detection;

/// <summary>
/// Reguła ruchu doklejona do <see cref="TriggerCondition"/> — pozwala odpalić trigger gdy
/// detekcja jedzie w niewłaściwą stronę albo zbyt szybko/wolno. Ewaluowana per-detekcja na
/// podstawie info z trackera (track_id + velocity + heading w przestrzeni metrycznej).
///
/// Wszystkie pola są opcjonalne — gdy żadne nie jest ustawione, reguła trywialnie przechodzi
/// (ale staje się bezużyteczna). Gdy reguła jest ustawiona ale detekcja nie ma jeszcze tracka
/// (pierwsza klatka po pojawieniu się obiektu) lub nie zebrała <see cref="MinTrackSamples"/>
/// próbek — detekcja jest **odrzucana** (bezpieczniej: nie palić triggera bez pewności).
/// </summary>
public sealed class MotionRule
{
    /// <summary>
    /// Oczekiwany kierunek ruchu w stopniach (0 = wzdłuż +X osi metrycznej z homografii,
    /// 90 = +Y, rosnąco przeciwnie do wskazówek zegara). Null = brak filtru kierunku.
    /// </summary>
    public double? ExpectedHeadingDegrees { get; set; }

    /// <summary>
    /// Tolerancja kierunku (połówka kąta) w stopniach. Detekcja przechodzi gdy
    /// |heading − expected| (zwinięte do [-180, 180]) ≤ tolerancja.
    /// 90° = "wszystko poza odwrotnym kierunkiem" (pół-przestrzeń).
    /// 45° = "tylko ten konkretny kierunek (±45°)".
    /// Ignorowana gdy <see cref="ExpectedHeadingDegrees"/> = null.
    /// </summary>
    public double DirectionToleranceDegrees { get; set; } = 90;

    /// <summary>Minimalna prędkość w m/s (włącznie). Null = brak dolnego limitu.</summary>
    public double? MinSpeedMps { get; set; }

    /// <summary>Maksymalna prędkość w m/s (włącznie). Null = brak górnego limitu.</summary>
    public double? MaxSpeedMps { get; set; }

    /// <summary>
    /// Minimalna liczba próbek w tracku (klatek z dopasowaniem) wymaganych żeby ewaluować ruch.
    /// Default 2 — pierwsza klatka po wykryciu nie ma jeszcze velocity. Dla głośnych
    /// detekcji można podnieść do 3 (smoothing).
    /// </summary>
    public int MinTrackSamples { get; set; } = 2;
}

/// <summary>
/// Czysta matematyka kątów na podłodze (homografia → metry). Konwencje:
///  • Kąt w stopniach, 0 = +X osi metrycznej, 90 = +Y, rosnąco CCW
///  • Wszystkie wartości są normalizowane do <c>[0, 360)</c>
///  • Różnica kątowa zwijana do <c>[-180, 180]</c>
/// </summary>
public static class MotionMath
{
    /// <summary>Normalizuje kąt do przedziału [0, 360).</summary>
    public static double NormalizeDegrees(double degrees)
    {
        var d = degrees % 360.0;
        if (d < 0) d += 360.0;
        return d;
    }

    /// <summary>
    /// Najmniejsza różnica kątowa zwinięta do [-180, 180]. Dodatnie = b leży CCW od a.
    /// </summary>
    public static double SignedDifferenceDegrees(double a, double b)
    {
        var diff = NormalizeDegrees(b - a + 540.0) - 180.0;
        return diff;
    }

    /// <summary>Bezwzględna różnica kątowa w [0, 180].</summary>
    public static double AbsoluteDifferenceDegrees(double a, double b)
        => Math.Abs(SignedDifferenceDegrees(a, b));

    /// <summary>
    /// Heading w stopniach z wektora prędkości <c>(vx, vy)</c> w metrach/sekundę.
    /// Zwraca null gdy magnitude wektora jest poniżej <paramref name="minMagnitude"/>
    /// (chronimy przed garbage z noisy bbox-ów obiektu stojącego w miejscu).
    /// </summary>
    public static double? HeadingFromVelocity(double vx, double vy, double minMagnitude = 1e-3)
    {
        var mag = Math.Sqrt(vx * vx + vy * vy);
        if (mag < minMagnitude) return null;
        var rad = Math.Atan2(vy, vx);
        return NormalizeDegrees(rad * 180.0 / Math.PI);
    }
}
