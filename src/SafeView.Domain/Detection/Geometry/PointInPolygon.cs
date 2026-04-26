namespace SafeView.Domain.Detection.Geometry;

/// <summary>
/// Klasyczny test „punkt wewnątrz wielokąta" — ray casting algorithm (Jordan curve theorem).
/// Obsługuje dowolne wielokąty (convex + concave), ale nie self-intersecting.
/// O(n) względem liczby wierzchołków — hot path detekcji.
/// </summary>
public static class PointInPolygon
{
    /// <summary>
    /// Zwraca true gdy punkt (px,py) leży wewnątrz wielokąta zdefiniowanego listą wierzchołków
    /// w kolejności (clockwise lub counter-clockwise, obojętnie).
    /// Punkt DOKŁADNIE na krawędzi zwraca wynik niedefiniowany (implementation-dependent),
    /// ale deterministyczny dla danych wejść.
    /// </summary>
    /// <param name="px">X punktu (znormalizowany 0-1)</param>
    /// <param name="py">Y punktu</param>
    /// <param name="polygon">Lista par (x,y) — minimum 3 wierzchołki</param>
    public static bool Contains(double px, double py, IReadOnlyList<(double X, double Y)> polygon)
    {
        if (polygon.Count < 3) return false;

        var inside = false;
        var n = polygon.Count;

        // Ray cast w prawo (kierunek +X). Liczymy przecięcia z krawędziami.
        // Parzyste przecięcia → punkt na zewnątrz, nieparzyste → wewnątrz.
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];

            // Warunek Jordana: krawędź przecina linię poziomą y=py między wierzchołkami
            var intersects = ((yi > py) != (yj > py)) &&
                             (px < (xj - xi) * (py - yi) / (yj - yi) + xi);

            if (intersects) inside = !inside;
        }

        return inside;
    }
}
