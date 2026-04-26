using SafeView.Application.Abstractions.Detection;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection;

/// <summary>
/// Wspólny helper matchujący <see cref="TriggerCondition"/> do <see cref="DetectionResult"/> —
/// używany zarówno w <see cref="DetectionPipeline"/> (zone-level filter) jak i w
/// <see cref="TriggerEvaluator"/> (AND-logic validation).
///
/// Obsługuje dwa równoległe path-y:
///  • <b>DetectionClass path</b> (nowy) — gdy <see cref="TriggerCondition.DetectionClassId"/>
///    jest ustawione, matching odbywa się przez rezolucję <see cref="DetectionClass"/>:
///    dla ClosedSetBinding po (ClosedSetModelId, ClosedSetLabel), dla Text/Visual/TextAndVisual
///    po TextPrompt (detektor open-vocab zwraca prompt jako Label).
///  • <b>Legacy path</b> — gdy DetectionClassId null, używamy pary
///    (<see cref="TriggerCondition.ModelId"/>, <see cref="TriggerCondition.Labels"/>).
///
/// Backward-compat: stare triggery (bez DetectionClassId) zachowują identyczne zachowanie.
/// </summary>
public static class TriggerConditionMatcher
{
    /// <summary>
    /// Czy <paramref name="detection"/> pasuje do <paramref name="cond"/>.
    /// </summary>
    /// <param name="cond">Warunek triggera.</param>
    /// <param name="detection">Pojedyncza detekcja z pipeline.</param>
    /// <param name="detectionClasses">Słownik klas pre-loadowanych przez pipeline.
    /// Może być null — wtedy DetectionClass path jest pomijany i używamy legacy.</param>
    /// <param name="enforceMinConfidence">Jeśli true, sprawdzane jest <see cref="TriggerCondition.MinConfidence"/>.
    /// Zone-filter używa false (filter po zoning, confidence gate w evaluatorze); evaluator używa true.</param>
    public static bool Matches(
        TriggerCondition cond,
        DetectionResult detection,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses,
        bool enforceMinConfidence)
    {
        ArgumentNullException.ThrowIfNull(cond);
        ArgumentNullException.ThrowIfNull(detection);

        if (!string.IsNullOrEmpty(cond.DetectionClassId)
            && detectionClasses is not null
            && detectionClasses.TryGetValue(cond.DetectionClassId!, out var klass))
        {
            if (!MatchesClass(klass, cond, detection)) return false;
        }
        else
        {
            if (detection.ModelId != cond.ModelId) return false;
            if (cond.Labels.Count > 0 && !cond.Labels.Contains(detection.Label)) return false;
        }

        if (enforceMinConfidence
            && cond.MinConfidence is not null
            && detection.Confidence < cond.MinConfidence.Value)
            return false;

        return true;
    }

    private static bool MatchesClass(DetectionClass klass, TriggerCondition cond, DetectionResult detection)
    {
        switch (klass.Kind)
        {
            case DetectionClassKind.ClosedSetBinding:
                if (string.IsNullOrEmpty(klass.ClosedSetModelId) || string.IsNullOrEmpty(klass.ClosedSetLabel))
                    return false;
                return detection.ModelId == klass.ClosedSetModelId
                    && string.Equals(detection.Label, klass.ClosedSetLabel, StringComparison.OrdinalIgnoreCase);

            case DetectionClassKind.Text:
            case DetectionClassKind.Visual:
            case DetectionClassKind.TextAndVisual:
                // Dla open-vocab warunek pasuje gdy:
                //   - jeśli cond.ModelId jest ustawione → model detekcji musi się zgadzać (user wybrał konkretny)
                //   - label detekcji == TextPrompt klasy (open-vocab detektor zwraca prompt jako Label).
                //     Dla Visual-only, gdy TextPrompt jest null — akceptujemy dowolny label pasujący
                //     do ModelId (YOLOE zwraca visual-match label z własnej nomenklatury).
                if (!string.IsNullOrEmpty(cond.ModelId) && detection.ModelId != cond.ModelId)
                    return false;
                if (!string.IsNullOrEmpty(klass.TextPrompt))
                    return string.Equals(detection.Label, klass.TextPrompt, StringComparison.OrdinalIgnoreCase);
                return true;

            default:
                return false;
        }
    }
}
