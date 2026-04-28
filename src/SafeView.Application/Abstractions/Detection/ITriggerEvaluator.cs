using SafeView.Domain.Detection;

namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Ewaluuje Trigger na detekcjach wewnątrz strefy. Odpowiada za:
///  • AND logic między warunkami (każdy <c>TriggerCondition</c> musi być spełniony)
///  • Stateful: <c>PersistentFrames</c> (N klatek z rzędu) i <c>Cooldown</c> (minimalny odstęp)
///  • Schedule (okno czasowe)
///
/// Implementacja jest thread-safe i singleton — trzyma stan per (TriggerId, ZoneId).
/// </summary>
public interface ITriggerEvaluator
{
    /// <summary>
    /// Sprawdza czy Trigger powinien wypalić na podanych detekcjach wewnątrz strefy
    /// (detekcje są już przefiltrowane przez <c>BboxRuleEvaluator</c>).
    /// Jeśli result.Fired == true → caller wywołuje ActionDispatcher.
    /// </summary>
    TriggerEvaluationResult Evaluate(
        Trigger trigger,
        string zoneId,
        IReadOnlyList<DetectionResult> detectionsInZone);

    /// <summary>
    /// Wariant z rezolucją <see cref="DetectionClass"/> — używany gdy trigger korzysta z nowego
    /// <see cref="TriggerCondition.DetectionClassId"/>. Słownik jest pre-loadowany przez
    /// <c>DetectionPipeline</c> jednym batch query per klatka.
    /// </summary>
    TriggerEvaluationResult Evaluate(
        Trigger trigger,
        string zoneId,
        IReadOnlyList<DetectionResult> detectionsInZone,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses);

    /// <summary>
    /// Wariant z dostępem do pełnej listy detekcji z klatki — wymagany do ewaluacji
    /// <see cref="TriggerCondition.Containment"/> (containment check potrzebuje obiektów
    /// poza strefą i poza warunkiem).
    /// </summary>
    TriggerEvaluationResult Evaluate(
        Trigger trigger,
        string zoneId,
        IReadOnlyList<DetectionResult> detectionsInZone,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses,
        IReadOnlyList<DetectionResult>? allDetections);

    /// <summary>
    /// Pełny wariant z info trackera — wymagany do ewaluacji <see cref="TriggerCondition.Motion"/>
    /// (kierunek/prędkość). <paramref name="tracks"/> mapuje DetectionResult → TrackedInfo,
    /// keyed by reference equality (caller dostarcza tę samą instancję detekcji).
    /// </summary>
    TriggerEvaluationResult Evaluate(
        Trigger trigger,
        string zoneId,
        IReadOnlyList<DetectionResult> detectionsInZone,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses,
        IReadOnlyList<DetectionResult>? allDetections,
        IReadOnlyDictionary<DetectionResult, TrackedInfo>? tracks);

    /// <summary>Czyści cały stan (przy restarcie lub zmianie konfiguracji).</summary>
    void Reset();
}
