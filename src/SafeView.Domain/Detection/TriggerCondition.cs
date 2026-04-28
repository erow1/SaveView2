namespace SafeView.Domain.Detection;

/// <summary>
/// Pojedynczy warunek Triggera — odwołuje się do konkretnego modelu i definiuje
/// kiedy ten model „zalicza trafienie" w strefie. Wiele TriggerCondition w jednym
/// <see cref="Trigger"/> łączy się przez AND.
/// </summary>
public sealed class TriggerCondition
{
    /// <summary>
    /// ID modelu ML który ma wykryć obiekty. Ignorowane gdy <see cref="DetectionClassId"/> jest
    /// ustawione i klasa ma kind = <c>Text</c>/<c>Visual</c>/<c>TextAndVisual</c> (open-vocab) —
    /// w tych przypadkach model wybiera pipeline na podstawie wymagań kapabilitetu klasy.
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// (Legacy) Klasy detekcji które się liczą (np. <c>["person"]</c>, <c>["person","no_helmet"]</c>).
    /// Pusta lista = wszystkie klasy z modelu. Używane gdy <see cref="DetectionClassId"/> nie jest ustawione.
    /// </summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>
    /// ID <c>DetectionClass</c> — nowy sposób referencji klasy detekcji, niezależny od konkretnego
    /// modelu. Gdy ustawione, pipeline rozstrzyga matching poprzez klasę (closed-set binding albo
    /// open-vocab prompt). Null = użyj legacy path (<see cref="ModelId"/> + <see cref="Labels"/>).
    /// </summary>
    public string? DetectionClassId { get; set; }

    /// <summary>Kryterium bbox-in-zone.</summary>
    public BboxRule BboxRule { get; set; } = BboxRule.CenterInZone;

    /// <summary>
    /// Ile narożników musi być w strefie (tylko dla <see cref="BboxRule.NCorners"/>).
    /// Wartości 1..4. Null dla innych trybów.
    /// </summary>
    public int? CornersRequired { get; set; }

    /// <summary>
    /// Próg IoU (0..1) dla <see cref="BboxRule.Iou"/>. Null dla innych trybów.
    /// </summary>
    public double? IouThreshold { get; set; }

    /// <summary>Override confidence threshold z modelu (null = użyj defaultu modelu).</summary>
    public double? MinConfidence { get; set; }

    /// <summary>Minimum detekcji tego typu w strefie żeby uznać warunek za spełniony.</summary>
    public int MinCount { get; set; } = 1;

    /// <summary>
    /// Opcjonalna reguła zawartości — sprawdza czy WEWNĄTRZ bbox-a tej detekcji znajduje się
    /// (lub nie) detekcja innej klasy. Pozwala wyrazić relacyjne warunki typu "person bez kasku"
    /// jednym condition-em. Null = brak filtru (zachowanie legacy).
    /// </summary>
    public ContainmentRule? Containment { get; set; }

    /// <summary>
    /// Opcjonalna reguła ruchu — sprawdza kierunek/prędkość detekcji (na bazie tracka). Wymaga
    /// homografii kamery i <c>IObjectTracker</c>-a podłączonego w pipeline. Null = brak filtru.
    /// </summary>
    public MotionRule? Motion { get; set; }
}
