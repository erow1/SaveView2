namespace SafeView.Domain.Detection.Geometry;

/// <summary>
/// Jeden tile (wycinek) w układzie pikseli obrazu źródłowego.
/// </summary>
public readonly record struct Tile(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Algorytm tilingu SAHI (Slicing Aided Hyper Inference) — dzieli obraz na kwadratowe
/// tiles o zadanym rozmiarze z overlapem. Każdy tile idzie osobno do modelu.
/// Detekcje z sąsiednich tiles są potem mergowane przez NMS, bo obiekty na granicach
/// są widoczne w dwóch tiles naraz.
///
/// Dlaczego overlap? Obiekt który wypadnie dokładnie na granicy tiles byłby ciętej w połowie
/// w obu tile-ach — model widziałby go jako 2 mniejsze obiekty. Overlap 20% gwarantuje,
/// że każdy punkt obrazu jest w pełni widoczny w co najmniej jednym tile.
///
/// Używany przez <c>SlicedDetector</c> w SafeView.ML.
/// </summary>
public static class TileGrid
{
    /// <summary>
    /// Generuje listę tiles pokrywających obraz <paramref name="imageWidth"/>×<paramref name="imageHeight"/>.
    /// </summary>
    /// <param name="imageWidth">Szerokość obrazu w pikselach.</param>
    /// <param name="imageHeight">Wysokość obrazu w pikselach.</param>
    /// <param name="tileSize">Rozmiar tile (kwadratowy) — typowo <c>model.InputSize</c>, np. 640.</param>
    /// <param name="overlap">Overlap 0..0.5 — ułamek tile-a nachodzący na sąsiedni (0.2 = 20%).</param>
    /// <returns>Lista tiles. Gdy obraz jest mniejszy niż tileSize, zwraca 1 tile o wymiarach obrazu.</returns>
    public static List<Tile> Generate(int imageWidth, int imageHeight, int tileSize, double overlap = 0.2)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || tileSize <= 0)
            return [];
        if (overlap < 0) overlap = 0;
        if (overlap >= 1) overlap = 0.5; // sanity; >=1 = infinite loop

        // Jeśli obraz mieści się w jednym tile — nie tile'ujemy
        if (imageWidth <= tileSize && imageHeight <= tileSize)
            return [new Tile(0, 0, imageWidth, imageHeight)];

        var step = Math.Max(1, (int)(tileSize * (1.0 - overlap)));
        var tiles = new List<Tile>();

        // Generujemy wszystkie tiles o dokładnym rozmiarze tileSize×tileSize.
        // Ostatni tile w każdym rzędzie/kolumnie jest przesuwany tak, żeby dotykał prawej/dolnej krawędzi
        // (żeby obraz był w 100% pokryty). Może to powodować większy overlap na ostatnim tile — OK, NMS się tym zajmie.
        var ys = ComputeStarts(imageHeight, tileSize, step);
        var xs = ComputeStarts(imageWidth, tileSize, step);

        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                var w = Math.Min(tileSize, imageWidth - x);
                var h = Math.Min(tileSize, imageHeight - y);
                tiles.Add(new Tile(x, y, w, h));
            }
        }

        return tiles;
    }

    /// <summary>
    /// Oblicza pozycje startowe tiles w jednym wymiarze żeby pokryć cały wymiar.
    /// Pierwszy tile zawsze zaczyna od 0, ostatni kończy się na <paramref name="imageSize"/>.
    /// </summary>
    private static List<int> ComputeStarts(int imageSize, int tileSize, int step)
    {
        if (imageSize <= tileSize)
            return [0];

        var starts = new List<int>();
        var pos = 0;
        while (pos + tileSize < imageSize)
        {
            starts.Add(pos);
            pos += step;
        }
        // Ostatni tile — przyczepiamy do prawej/dolnej krawędzi
        starts.Add(imageSize - tileSize);
        return starts;
    }

    /// <summary>
    /// Heurystyka wyboru strategii inferencji na podstawie rozmiaru ROI i modelu.
    /// Używane dla trybu <see cref="RoiInferenceMode.Adaptive"/>.
    /// </summary>
    /// <param name="roiWidth">Szerokość crop ROI w pikselach.</param>
    /// <param name="roiHeight">Wysokość crop ROI w pikselach.</param>
    /// <param name="modelInputSize">Input modelu (np. 640).</param>
    /// <returns>
    ///  • <see cref="RoiInferenceMode.Native"/> gdy ROI ≤ modelInputSize (zero tilingu, zero resize)
    ///  • <see cref="RoiInferenceMode.Resize"/> gdy ROI trochę większe (do 2× modelInputSize) — prosty resize OK
    ///  • <see cref="RoiInferenceMode.Sliced"/> gdy ROI >> modelInputSize — SAHI konieczne dla drobnych obiektów
    /// </returns>
    public static RoiInferenceMode PickAdaptiveMode(int roiWidth, int roiHeight, int modelInputSize)
    {
        var maxDim = Math.Max(roiWidth, roiHeight);
        if (maxDim <= modelInputSize) return RoiInferenceMode.Native;
        if (maxDim <= modelInputSize * 2) return RoiInferenceMode.Resize;
        return RoiInferenceMode.Sliced;
    }
}
