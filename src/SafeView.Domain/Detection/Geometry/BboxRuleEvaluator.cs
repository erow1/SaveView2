namespace SafeView.Domain.Detection.Geometry;

/// <summary>
/// Decyduje czy detekcja (bbox) „wpada do strefy" według wybranego trybu <see cref="BboxRule"/>.
/// Pure function, stateless — bezpieczne do wywołania z wielu wątków.
/// </summary>
public static class BboxRuleEvaluator
{
    /// <summary>
    /// Sprawdza czy <paramref name="bbox"/> spełnia kryterium <paramref name="rule"/>
    /// względem wielokąta strefy <paramref name="zonePolygon"/>.
    /// </summary>
    /// <param name="rule">Tryb decyzji.</param>
    /// <param name="bbox">Bounding box detekcji (znormalizowany 0-1).</param>
    /// <param name="zonePolygon">Wielokąt strefy (znormalizowany 0-1).</param>
    /// <param name="cornersRequired">Liczba wymaganych narożników dla <see cref="BboxRule.NCorners"/> (1-4).</param>
    /// <param name="iouThreshold">Próg IoU dla <see cref="BboxRule.Iou"/> (0-1).</param>
    public static bool Evaluate(
        BboxRule rule,
        Bbox bbox,
        IReadOnlyList<(double X, double Y)> zonePolygon,
        int? cornersRequired = null,
        double? iouThreshold = null)
    {
        return rule switch
        {
            BboxRule.CenterInZone => EvaluateCenterInZone(bbox, zonePolygon),
            BboxRule.AnyCorner => EvaluateCornerCount(bbox, zonePolygon) >= 1,
            BboxRule.AllCorners => EvaluateCornerCount(bbox, zonePolygon) == 4,
            BboxRule.NCorners => EvaluateCornerCount(bbox, zonePolygon) >= (cornersRequired ?? 2),
            BboxRule.Iou => EvaluateIou(bbox, zonePolygon, iouThreshold ?? 0.3),
            _ => false
        };
    }

    private static bool EvaluateCenterInZone(Bbox bbox, IReadOnlyList<(double X, double Y)> polygon)
        => PointInPolygon.Contains(bbox.CenterX, bbox.CenterY, polygon);

    /// <summary>Zwraca ile z 4 narożników bbox leży wewnątrz wielokąta strefy.</summary>
    private static int EvaluateCornerCount(Bbox bbox, IReadOnlyList<(double X, double Y)> polygon)
    {
        var count = 0;
        foreach (var (cx, cy) in bbox.Corners())
            if (PointInPolygon.Contains(cx, cy, polygon)) count++;
        return count;
    }

    /// <summary>
    /// Przybliżona IoU — liczona przeciwko axis-aligned bounding rect wielokąta (nie samym polygonie).
    /// To kompromis między dokładnością a wydajnością — pełny polygon IoU wymagałby algorytmu
    /// Sutherland-Hodgman, drogiego w hot path. Dla typowych stref (quasi-rect) różnica jest marginalna.
    /// </summary>
    private static bool EvaluateIou(Bbox bbox, IReadOnlyList<(double X, double Y)> polygon, double threshold)
    {
        if (polygon.Count == 0) return false;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in polygon)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        var polyBbox = new Bbox(minX, minY, maxX - minX, maxY - minY);
        var intersection = bbox.IntersectionArea(polyBbox);
        if (intersection <= 0) return false;

        var union = bbox.Area + polyBbox.Area - intersection;
        if (union <= 0) return false;

        return intersection / union >= threshold;
    }
}
