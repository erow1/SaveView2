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
        => Matches(cond, detection, detectionClasses, enforceMinConfidence,
            allDetections: null, tracks: null);

    /// <summary>
    /// Wariant z dostępem do listy wszystkich detekcji z klatki — wymagany do ewaluacji
    /// <see cref="TriggerCondition.Containment"/>. Gdy <paramref name="allDetections"/> null
    /// i warunek ma containment, filtr jest pomijany (zachowanie defensive — nie blokujemy
    /// triggera gdy caller nie dostarczył kontekstu).
    /// </summary>
    public static bool Matches(
        TriggerCondition cond,
        DetectionResult detection,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses,
        bool enforceMinConfidence,
        IReadOnlyList<DetectionResult>? allDetections)
        => Matches(cond, detection, detectionClasses, enforceMinConfidence,
            allDetections, tracks: null);

    /// <summary>
    /// Wariant z info trackera — wymagany do ewaluacji <see cref="TriggerCondition.Motion"/>.
    /// Gdy <paramref name="tracks"/> null albo nie zawiera tej detekcji a warunek ma motion rule,
    /// detekcja jest **odrzucana** (motion rule wymaga pozytywnej weryfikacji prędkości/kierunku
    /// — domyślne "fail-closed" jest bezpieczniejsze niż palenie triggera bez danych).
    /// </summary>
    public static bool Matches(
        TriggerCondition cond,
        DetectionResult detection,
        IReadOnlyDictionary<string, DetectionClass>? detectionClasses,
        bool enforceMinConfidence,
        IReadOnlyList<DetectionResult>? allDetections,
        IReadOnlyDictionary<DetectionResult, TrackedInfo>? tracks)
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

        if (cond.Containment is { } rule && allDetections is not null
            && !ContainmentHolds(detection, rule, allDetections, detectionClasses))
            return false;

        if (cond.Motion is { } motion && !MotionHolds(detection, motion, tracks))
            return false;

        return true;
    }

    /// <summary>
    /// Czy reguła ruchu jest spełniona dla detekcji. Wymaga info z trackera (dict tracks).
    /// Fail-closed: brak tracka albo zbyt mało próbek = nie matchuje.
    /// </summary>
    private static bool MotionHolds(
        DetectionResult detection,
        MotionRule rule,
        IReadOnlyDictionary<DetectionResult, TrackedInfo>? tracks)
    {
        if (tracks is null || !tracks.TryGetValue(detection, out var info)) return false;

        if (info.SamplesInTrack < Math.Max(2, rule.MinTrackSamples)) return false;

        if (rule.MinSpeedMps is { } minS)
        {
            if (info.SpeedMps is null || info.SpeedMps.Value < minS) return false;
        }
        if (rule.MaxSpeedMps is { } maxS)
        {
            if (info.SpeedMps is null || info.SpeedMps.Value > maxS) return false;
        }

        if (rule.ExpectedHeadingDegrees is { } expected)
        {
            if (info.HeadingDegrees is null) return false;
            var diff = MotionMath.AbsoluteDifferenceDegrees(expected, info.HeadingDegrees.Value);
            if (diff > rule.DirectionToleranceDegrees) return false;
        }

        return true;
    }

    /// <summary>
    /// Czy reguła zawartości jest spełniona dla detekcji A (subject). Iteruje wszystkie inne
    /// detekcje z klatki, wybiera te które matchują "klasę B" z reguły, sprawdza czy są
    /// geometrycznie wewnątrz subject-a, a potem aplikuje operator (Any/None).
    /// </summary>
    private static bool ContainmentHolds(
        DetectionResult subject,
        ContainmentRule rule,
        IReadOnlyList<DetectionResult> all,
        IReadOnlyDictionary<string, DetectionClass>? classes)
    {
        var hasInsider = false;
        foreach (var other in all)
        {
            if (ReferenceEquals(other, subject)) continue;
            if (!MatchesOther(other, rule, classes)) continue;
            if (!ContainmentChecker.IsContained(other.Bbox, subject.Bbox, rule.Criterion, rule.IoMinThreshold))
                continue;

            hasInsider = true;
            break;
        }

        return rule.Operator switch
        {
            ContainmentOperator.ContainsAny => hasInsider,
            ContainmentOperator.ContainsNone => !hasInsider,
            _ => false
        };
    }

    private static bool MatchesOther(
        DetectionResult det,
        ContainmentRule rule,
        IReadOnlyDictionary<string, DetectionClass>? classes)
    {
        if (!string.IsNullOrEmpty(rule.OtherDetectionClassId)
            && classes is not null
            && classes.TryGetValue(rule.OtherDetectionClassId!, out var klass))
        {
            return MatchesOtherClass(klass, det);
        }

        if (string.IsNullOrEmpty(rule.OtherModelId)) return false;
        if (det.ModelId != rule.OtherModelId) return false;
        if (rule.OtherLabels.Count > 0 && !rule.OtherLabels.Contains(det.Label)) return false;
        return true;
    }

    private static bool MatchesOtherClass(DetectionClass klass, DetectionResult det)
    {
        switch (klass.Kind)
        {
            case DetectionClassKind.ClosedSetBinding:
                if (string.IsNullOrEmpty(klass.ClosedSetModelId) || string.IsNullOrEmpty(klass.ClosedSetLabel))
                    return false;
                return det.ModelId == klass.ClosedSetModelId
                    && string.Equals(det.Label, klass.ClosedSetLabel, StringComparison.OrdinalIgnoreCase);

            case DetectionClassKind.Text:
            case DetectionClassKind.Visual:
            case DetectionClassKind.TextAndVisual:
                if (!string.IsNullOrEmpty(klass.TextPrompt))
                    return string.Equals(det.Label, klass.TextPrompt, StringComparison.OrdinalIgnoreCase);
                return true;

            default:
                return false;
        }
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
