using SafeView.Domain.Common;

namespace SafeView.Domain.Detection;

/// <summary>
/// Trigger — globalny, re-używalny zespół warunków wyzwalających akcje.
/// Przypinany do stref (<c>Zone.TriggerIds</c>). Jeden trigger może być używany w wielu strefach.
///
/// Semantyka warunków: wszystkie <see cref="Conditions"/> muszą być spełnione równocześnie
/// w tej samej klatce, w obrębie tej samej strefy (AND logic).
/// </summary>
public sealed class Trigger : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Warunki łączone przez AND — wszystkie muszą pasować.</summary>
    public List<TriggerCondition> Conditions { get; set; } = [];

    /// <summary>
    /// Minimalny odstęp między dwoma odpaleniami tego triggera dla tej samej strefy.
    /// Chroni przed powodzią SMS/email. 0 = bez cooldownu.
    /// </summary>
    public int CooldownSeconds { get; set; } = 30;

    /// <summary>
    /// Wymaga N klatek z rzędu ze spełnionymi warunkami zanim Trigger wypali.
    /// 1 = pojedyncza klatka wystarczy. Redukuje false positives z flickering detekcji.
    /// </summary>
    public int PersistentFrames { get; set; } = 1;

    /// <summary>Okno czasowe — null = trigger aktywny 24/7.</summary>
    public TriggerSchedule? Schedule { get; set; }

    /// <summary>Akcje do wykonania gdy wszystkie warunki spełnione (+ ew. VLLM potwierdzi).</summary>
    public List<string> ActionIds { get; set; } = [];

    /// <summary>
    /// Opcjonalna walidacja kontekstowa przez VLLM — redukuje false-positives.
    /// Gdy <see cref="VllmCheckConfig.Enabled"/> == true, akcje odpalają się tylko
    /// gdy VLLM zwróci <c>confirmed: true</c>. Null lub Enabled=false = czysty flow Fazy 1.
    /// </summary>
    public VllmCheckConfig? VllmCheck { get; set; }

    /// <summary>
    /// Filtry przestrzenne (dystans w metrach między parami obiektów) — wymagają skalibrowanej
    /// kamery (<c>Camera.CalibrationPoints</c>). Pusta lista = brak filtrów, trigger zachowuje się
    /// jak w Fazie 1. Wszystkie filtry muszą przejść (AND) aby trigger wypalił.
    /// </summary>
    public List<SpatialFilter> SpatialFilters { get; set; } = [];
}
