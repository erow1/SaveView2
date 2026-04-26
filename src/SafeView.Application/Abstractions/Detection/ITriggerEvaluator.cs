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

    /// <summary>Czyści cały stan (przy restarcie lub zmianie konfiguracji).</summary>
    void Reset();
}
