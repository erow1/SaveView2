using SafeView.Domain.Detection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SafeView.ML.SlicedInference;

/// <summary>
/// Wycinek ROI z klatki — wraz z metadanymi potrzebnymi do transformacji współrzędnych.
/// </summary>
/// <param name="CropPath">Ścieżka tempfile z wycinkiem JPG (caller MUSI go usunąć po użyciu).</param>
/// <param name="OffsetX">Przesunięcie cropa w pikselach od lewej krawędzi oryginalnej klatki.</param>
/// <param name="OffsetY">Przesunięcie od góry.</param>
/// <param name="CropWidth">Szerokość cropa w pikselach.</param>
/// <param name="CropHeight">Wysokość cropa w pikselach.</param>
/// <param name="OriginalWidth">Szerokość oryginalnej klatki — do normalizacji detekcji do [0..1] kamery.</param>
/// <param name="OriginalHeight">Wysokość oryginalnej klatki.</param>
public sealed record RoiCrop(
    string CropPath,
    int OffsetX,
    int OffsetY,
    int CropWidth,
    int CropHeight,
    int OriginalWidth,
    int OriginalHeight);

/// <summary>
/// Wycina ROI z klatki kamery do tempfile, żeby model dostał tylko istotny fragment
/// (nie całą klatkę 4K). Zapewnia też metadane do transformacji współrzędnych detekcji
/// z układu cropa do układu [0..1] kamery.
/// </summary>
public static class RoiCropper
{
    private static readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "safeview-roi");

    /// <summary>
    /// Wycina prostokąt ROI z klatki i zapisuje jako tempfile JPG.
    /// Caller jest odpowiedzialny za <see cref="RoiCrop.CropPath"/> po zakończeniu.
    /// </summary>
    public static async Task<RoiCrop> CropAsync(string framePath, RoiRectangle roi, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_tempRoot);
        using var image = await Image.LoadAsync<Rgb24>(framePath, ct).ConfigureAwait(false);

        var srcW = image.Width;
        var srcH = image.Height;

        // Konwersja znormalizowanych [0..1] → piksele + clamp do granic obrazu
        var x = Math.Clamp((int)Math.Round(roi.X * srcW), 0, srcW - 1);
        var y = Math.Clamp((int)Math.Round(roi.Y * srcH), 0, srcH - 1);
        var w = Math.Clamp((int)Math.Round(roi.Width * srcW), 1, srcW - x);
        var h = Math.Clamp((int)Math.Round(roi.Height * srcH), 1, srcH - y);

        var cropPath = Path.Combine(_tempRoot, $"roi_{Guid.NewGuid():N}.jpg");

        using (var crop = image.Clone(ctx => ctx.Crop(new Rectangle(x, y, w, h))))
        {
            await crop.SaveAsJpegAsync(cropPath, ct).ConfigureAwait(false);
        }

        return new RoiCrop(cropPath, x, y, w, h, srcW, srcH);
    }

    /// <summary>
    /// Wycina prostokąt w pikselach z paddingiem (Faza 5 cascade). Padding jest dodawany
    /// proporcjonalnie do wymiarów bbox; jeśli wynikowy crop jest mniejszy niż <paramref name="minSize"/>,
    /// padding jest zwiększany do min rozmiaru. Clamp do granic klatki.
    /// </summary>
    public static async Task<RoiCrop> CropBboxAsync(
        string framePath,
        int bboxX, int bboxY, int bboxW, int bboxH,
        double paddingRatio = 0.2,
        int minSize = 128,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_tempRoot);
        using var image = await Image.LoadAsync<Rgb24>(framePath, ct).ConfigureAwait(false);
        var srcW = image.Width;
        var srcH = image.Height;

        // Padding proporcjonalny
        var padX = (int)Math.Round(bboxW * paddingRatio);
        var padY = (int)Math.Round(bboxH * paddingRatio);

        var x = bboxX - padX;
        var y = bboxY - padY;
        var w = bboxW + 2 * padX;
        var h = bboxH + 2 * padY;

        // Clamp i zapewnij min rozmiar (ważne dla małych bboxów — confirmer potrzebuje "kontekstu")
        if (w < minSize) { x -= (minSize - w) / 2; w = minSize; }
        if (h < minSize) { y -= (minSize - h) / 2; h = minSize; }

        // Clamp do granic klatki
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        w = Math.Min(srcW - x, w);
        h = Math.Min(srcH - y, h);
        if (w < 1) w = 1;
        if (h < 1) h = 1;

        var cropPath = Path.Combine(_tempRoot, $"bbox_{Guid.NewGuid():N}.jpg");
        using (var crop = image.Clone(ctx => ctx.Crop(new Rectangle(x, y, w, h))))
        {
            await crop.SaveAsJpegAsync(cropPath, ct).ConfigureAwait(false);
        }

        return new RoiCrop(cropPath, x, y, w, h, srcW, srcH);
    }

    /// <summary>Usuwa tempfile cropa (safe — nie rzuca jeśli już nie istnieje).</summary>
    public static void Cleanup(RoiCrop crop)
    {
        try { if (File.Exists(crop.CropPath)) File.Delete(crop.CropPath); }
        catch { /* best effort */ }
    }
}
