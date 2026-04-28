using System.Collections.Concurrent;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Time;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection;

/// <summary>
/// Produkcyjna implementacja <see cref="ITriggerEvaluator"/>. Singleton — trzyma
/// stan per (TriggerId, ZoneId) w ConcurrentDictionary dla thread-safety.
///
/// Stan jest in-memory, tracony przy restarcie aplikacji — wszystkie triggery zaczynają
/// od zera (PersistentFrames = 0, LastFired = never).
/// </summary>
public sealed class TriggerEvaluator : ITriggerEvaluator
{
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<(string triggerId, string zoneId), EvaluatorState> _state = new();

    private sealed class EvaluatorState
    {
        public DateTime? LastFiredAt;
        public int ConsecutiveHits;
    }

    public TriggerEvaluator(IClock clock)
    {
        _clock = clock;
    }

    public TriggerEvaluationResult Evaluate(
        Trigger trigger,
        string zoneId,
        IReadOnlyList<DetectionResult> detectionsInZone)
        => Evaluate(trigger, zoneId, detectionsInZone, detectionClasses: null, allDetections: null, tracks: null);

    public TriggerEvaluationResult Evaluate(
        Trigger trigger,
        string zoneId,
        IReadOnlyList<DetectionResult> detectionsInZone,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses)
        => Evaluate(trigger, zoneId, detectionsInZone, detectionClasses, allDetections: null, tracks: null);

    public TriggerEvaluationResult Evaluate(
        Trigger trigger,
        string zoneId,
        IReadOnlyList<DetectionResult> detectionsInZone,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses,
        IReadOnlyList<DetectionResult>? allDetections)
        => Evaluate(trigger, zoneId, detectionsInZone, detectionClasses, allDetections, tracks: null);

    public TriggerEvaluationResult Evaluate(
        Trigger trigger,
        string zoneId,
        IReadOnlyList<DetectionResult> detectionsInZone,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses,
        IReadOnlyList<DetectionResult>? allDetections,
        IReadOnlyDictionary<DetectionResult, TrackedInfo>? tracks)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        if (!trigger.Enabled)
            return TriggerEvaluationResult.Skipped(ActionSkipReason.TriggerDisabled);

        if (trigger.Conditions.Count == 0)
            return TriggerEvaluationResult.Skipped(ActionSkipReason.None, "no conditions configured");

        var now = _clock.UtcNow;

        // 1. Schedule check
        if (trigger.Schedule is not null && !trigger.Schedule.IsActiveAt(now))
            return TriggerEvaluationResult.Skipped(ActionSkipReason.Schedule);

        // 2. Wszystkie warunki (AND) — czy pasują do detekcji w tej klatce
        var conditionsMet = AllConditionsMatch(trigger.Conditions, detectionsInZone, detectionClasses, allDetections, tracks);

        var key = (trigger.Id, zoneId);
        var state = _state.GetOrAdd(key, _ => new EvaluatorState());

        if (!conditionsMet)
        {
            // Reset licznika persistent frames gdy warunki nie pasują
            lock (state)
            {
                state.ConsecutiveHits = 0;
            }
            return TriggerEvaluationResult.Skipped(ActionSkipReason.None, "conditions not met");
        }

        // 3. PersistentFrames — inkrementujemy licznik; dopiero po N klatkach odpalamy
        int hitsNow;
        lock (state)
        {
            state.ConsecutiveHits++;
            hitsNow = state.ConsecutiveHits;
        }

        if (hitsNow < Math.Max(1, trigger.PersistentFrames))
            return TriggerEvaluationResult.Skipped(
                ActionSkipReason.None,
                $"persistent frames {hitsNow}/{trigger.PersistentFrames}");

        // 4. Cooldown — czy minął minimalny odstęp od ostatniego odpalenia
        if (trigger.CooldownSeconds > 0 && state.LastFiredAt is { } last)
        {
            var elapsed = (now - last).TotalSeconds;
            if (elapsed < trigger.CooldownSeconds)
                return TriggerEvaluationResult.Skipped(
                    ActionSkipReason.Cooldown,
                    $"cooldown {elapsed:N1}s / {trigger.CooldownSeconds}s");
        }

        // 5. Fire — zapisz LastFired + zresetuj licznik żeby następne odpalenie wymagało znów N klatek
        lock (state)
        {
            state.LastFiredAt = now;
            state.ConsecutiveHits = 0;
        }

        return TriggerEvaluationResult.Ok();
    }

    public void Reset() => _state.Clear();

    /// <summary>
    /// AND logic: wszystkie warunki muszą być spełnione w tej samej klatce.
    /// Matching delegowany do <see cref="TriggerConditionMatcher"/> — obsługuje zarówno legacy
    /// (ModelId+Labels) jak i nowy <see cref="TriggerCondition.DetectionClassId"/> path.
    /// </summary>
    private static bool AllConditionsMatch(
        IReadOnlyList<TriggerCondition> conditions,
        IReadOnlyList<DetectionResult> detections,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses,
        IReadOnlyList<DetectionResult>? allDetections,
        IReadOnlyDictionary<DetectionResult, TrackedInfo>? tracks)
    {
        foreach (var cond in conditions)
        {
            var matching = detections.Count(d =>
                TriggerConditionMatcher.Matches(cond, d, detectionClasses, enforceMinConfidence: true, allDetections, tracks));

            if (matching < Math.Max(1, cond.MinCount))
                return false;
        }

        return true;
    }
}
