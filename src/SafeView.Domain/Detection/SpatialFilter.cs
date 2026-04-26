namespace SafeView.Domain.Detection;

/// <summary>Rodzaj filtra przestrzennego — na razie tylko dystans między parami obiektów.</summary>
public enum SpatialFilterKind
{
    /// <summary>Dystans między parami detekcji (modelA/labelA × modelB/labelB) na podłodze w metrach.</summary>
    PairDistance = 0
}

/// <summary>Jak interpretować wiele par gdy modeli/detekcji jest wiele.</summary>
public enum SpatialPairMode
{
    /// <summary>Wystarczy że JEDNA para spełnia warunek (MIN/MAX). Typowe dla "ktoś blisko wózka".</summary>
    AnyPair = 0,

    /// <summary>WSZYSTKIE pary muszą spełniać warunek. Typowe dla "wszystkie osoby dalej niż 3 m od maszyny".</summary>
    AllPairs = 1
}

/// <summary>
/// Filtr przestrzenny doklejany do triggera — po spełnieniu klasycznych <see cref="TriggerCondition"/>
/// (AND), każdy <see cref="SpatialFilter"/> dodatkowo musi się zgodzić (AND między filtrami).
/// Działa na detekcjach z bieżącej klatki (nie tylko ze strefy) — pozwala łapać zdarzenia typu
/// "osoba w strefie zagrożenia + wózek dokądkolwiek indziej".
///
/// Wymaga skalibrowanej kamery (<see cref="SafeView.Domain.Cameras.Camera.CalibrationPoints"/>);
/// gdy kalibracji brak, filtr jest pomijany (trigger NIE wypala) + log Warning.
/// </summary>
public sealed class SpatialFilter
{
    public SpatialFilterKind Kind { get; set; } = SpatialFilterKind.PairDistance;

    // ── Para modeli/etykiet ──────────────────────────────────────────────────
    /// <summary>Pierwsza strona pary — ID modelu ML. Może być == <see cref="ModelIdB"/> (self-pair).</summary>
    public string ModelIdA { get; set; } = string.Empty;

    /// <summary>Etykiety modelu A — pusta lista = dowolna klasa z tego modelu.</summary>
    public List<string> LabelsA { get; set; } = [];

    public string ModelIdB { get; set; } = string.Empty;

    public List<string> LabelsB { get; set; } = [];

    // ── Zakres dystansu (metry) ──────────────────────────────────────────────
    /// <summary>Minimalny dystans (m) — pair musi być co najmniej tak daleko. null = brak dolnego ograniczenia.</summary>
    public double? MinDistanceM { get; set; }

    /// <summary>Maksymalny dystans (m) — pair musi być co najwyżej tak blisko. null = brak górnego ograniczenia.</summary>
    public double? MaxDistanceM { get; set; }

    public SpatialPairMode PairMode { get; set; } = SpatialPairMode.AnyPair;
}
