namespace SafeView.Domain.Detection.Geometry;

/// <summary>
/// Non-Max Suppression (NMS) — klasyczny algorytm mergujący zduplikowane detekcje.
///
/// Po tilingu SAHI ten sam obiekt może być wykryty w 2-3 sąsiednich tiles (bo tiles
/// nachodzą na siebie przez overlap). Bez NMS mielibyśmy dla każdego obiektu 2-3
/// bboxy które się prawie pokrywają.
///
/// Algorytm (O(n²) dla n detekcji):
///  1. Sortuj detekcje po confidence (malejąco)
///  2. Dla każdej detekcji od góry:
///     • zachowaj ją
///     • usuń wszystkie pozostałe o tej samej klasie które mają IoU &gt; threshold z tą
///
/// Pure function, stateless — bezpieczne do wywołania z wielu wątków.
/// </summary>
public static class NonMaxSuppression
{
    /// <summary>
    /// Wynikowa detekcja po NMS — identyfikator klasy, confidence, bbox, oryginalny index.
    /// </summary>
    public readonly record struct NmsCandidate(int ClassId, double Confidence, Bbox Bbox, int OriginalIndex);

    /// <summary>
    /// Usuwa zduplikowane detekcje. NMS jest **per-klasa** — dwie detekcje różnych klas
    /// nie kolidują ze sobą nawet gdy się nakładają (np. "person" i "helmet" mogą być w tym samym miejscu).
    /// </summary>
    /// <param name="detections">Lista kandydatów — dowolna kolejność.</param>
    /// <param name="iouThreshold">Próg IoU powyżej którego uznajemy detekcje za zduplikowane. Typowo 0.45.</param>
    /// <returns>Zredukowana lista — tylko non-duplicate.</returns>
    public static List<NmsCandidate> Apply(
        IReadOnlyList<NmsCandidate> detections,
        double iouThreshold = 0.45)
    {
        if (detections.Count <= 1) return [.. detections];

        // Grupuj per klasa — NMS jest per-class
        var result = new List<NmsCandidate>(detections.Count);
        foreach (var group in detections.GroupBy(d => d.ClassId))
        {
            // Sortuj po confidence malejąco (.OrderByDescending jest stable)
            var sorted = group.OrderByDescending(d => d.Confidence).ToList();
            var suppressed = new bool[sorted.Count];

            for (int i = 0; i < sorted.Count; i++)
            {
                if (suppressed[i]) continue;
                result.Add(sorted[i]);

                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (suppressed[j]) continue;
                    if (IoU(sorted[i].Bbox, sorted[j].Bbox) > iouThreshold)
                        suppressed[j] = true;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Intersection over Union dla dwóch axis-aligned bounding boxes.
    /// Wynik w [0..1]. 0 = rozłączne, 1 = identyczne.
    /// </summary>
    public static double IoU(Bbox a, Bbox b)
    {
        var intersection = a.IntersectionArea(b);
        if (intersection <= 0) return 0;
        var union = a.Area + b.Area - intersection;
        return union > 0 ? intersection / union : 0;
    }
}
