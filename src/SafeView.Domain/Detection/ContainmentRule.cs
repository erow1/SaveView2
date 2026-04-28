using SafeView.Domain.Detection.Geometry;

namespace SafeView.Domain.Detection;

/// <summary>Operator filtra zawartości — czy wewnątrz bbox-a A musi BYĆ czy NIE BYĆ obiekt B.</summary>
public enum ContainmentOperator
{
    /// <summary>Wewnątrz bbox-a A musi być co najmniej jedno B (np. "person z telefonem").</summary>
    ContainsAny = 0,

    /// <summary>Wewnątrz bbox-a A nie może być żadne B (np. "person bez kasku").</summary>
    ContainsNone = 1
}

/// <summary>Kryterium "B jest wewnątrz A".</summary>
public enum ContainmentCriterion
{
    /// <summary>Środek bbox-a B leży wewnątrz bbox-a A. Robust dla kasku na osobie.</summary>
    Center = 0,

    /// <summary>area(A∩B) / min(area(A), area(B)) >= próg. Bardziej precyzyjne niż Center.</summary>
    IoMin = 1,

    /// <summary>B w 100% wewnątrz A (cały bbox B mieści się w A).</summary>
    Fully = 2
}

/// <summary>
/// Reguła zawartości doklejona do <see cref="TriggerCondition"/>. Dla każdej detekcji A pasującej
/// do warunku, sprawdza czy wewnątrz jej bbox-a ZNAJDUJE SIĘ (lub NIE) detekcja innej klasy B.
/// Dzięki temu warunek "person bez kasku" da się wyrazić jednym condition-em zamiast łączyć dwa
/// modele zewnętrznie.
///
/// Per-detection semantyka: detekcja A która nie spełnia containment "wypada" z listy matchujących
/// condition-a — wpada to w istniejący licznik <see cref="TriggerCondition.MinCount"/>.
/// </summary>
public sealed class ContainmentRule
{
    public ContainmentOperator Operator { get; set; } = ContainmentOperator.ContainsNone;

    public ContainmentCriterion Criterion { get; set; } = ContainmentCriterion.Center;

    /// <summary>
    /// Próg dla kryterium <see cref="ContainmentCriterion.IoMin"/> — jaki % min(area(A), area(B))
    /// musi nakładać się żeby uznać B za "wewnątrz" A. Ignorowany dla Center / Fully.
    /// </summary>
    public double IoMinThreshold { get; set; } = 0.7;

    // Referencja klasy B — analogicznie do TriggerCondition: nowy DetectionClass path albo legacy
    // ModelId+Labels. DetectionClassId ma priorytet gdy ustawione i klasa istnieje w słowniku.

    /// <summary>ID <c>DetectionClass</c> klasy B. Null = użyj legacy path.</summary>
    public string? OtherDetectionClassId { get; set; }

    /// <summary>(Legacy) ID modelu który wykrywa B. Używane gdy <see cref="OtherDetectionClassId"/> null.</summary>
    public string OtherModelId { get; set; } = string.Empty;

    /// <summary>(Legacy) Etykiety klasy B. Pusta lista = dowolna klasa z modelu B.</summary>
    public List<string> OtherLabels { get; set; } = [];
}

/// <summary>
/// Czysta geometria — sprawdza czy <paramref name="inner"/> jest "zawarty" w <paramref name="outer"/>
/// według wybranego kryterium. Bbox-y w znormalizowanych koordynatach [0..1].
/// </summary>
public static class ContainmentChecker
{
    public static bool IsContained(Bbox inner, Bbox outer, ContainmentCriterion criterion, double ioMinThreshold = 0.7)
    {
        return criterion switch
        {
            ContainmentCriterion.Center => CenterInside(inner, outer),
            ContainmentCriterion.IoMin => IoMinPasses(inner, outer, ioMinThreshold),
            ContainmentCriterion.Fully => FullyInside(inner, outer),
            _ => false
        };
    }

    private static bool CenterInside(Bbox inner, Bbox outer)
    {
        var cx = inner.CenterX;
        var cy = inner.CenterY;
        return cx >= outer.X && cx <= outer.Right
            && cy >= outer.Y && cy <= outer.Bottom;
    }

    private static bool IoMinPasses(Bbox inner, Bbox outer, double threshold)
    {
        var minArea = Math.Min(inner.Area, outer.Area);
        if (minArea <= 0) return false;
        var inter = inner.IntersectionArea(outer);
        return inter / minArea >= threshold;
    }

    private static bool FullyInside(Bbox inner, Bbox outer)
    {
        return inner.X >= outer.X
            && inner.Y >= outer.Y
            && inner.Right <= outer.Right
            && inner.Bottom <= outer.Bottom;
    }
}
