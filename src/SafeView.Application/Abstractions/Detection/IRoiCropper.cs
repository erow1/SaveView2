using SafeView.Domain.Detection;

namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Wycięcie ROI z klatki — ścieżka do tempfile + metadane offset-u potrzebne do transformacji
/// detekcji z układu cropa z powrotem do układu klatki kamery.
/// </summary>
public sealed record RoiCropResult(
    string CropPath,
    int OffsetX,
    int OffsetY,
    int CropWidth,
    int CropHeight,
    int OriginalWidth,
    int OriginalHeight);

/// <summary>
/// Wycina ROI z pełnej klatki kamery. Tempfile zapisywany na dysk — caller odpowiada
/// za jego usunięcie (metoda <see cref="Cleanup"/>).
/// Implementacja w <c>SafeView.ML</c> używa ImageSharp.
/// </summary>
public interface IRoiCropper
{
    /// <summary>Wycina znormalizowane ROI (0..1 koordynaty).</summary>
    Task<RoiCropResult> CropAsync(string framePath, RoiRectangle roi, CancellationToken ct = default);

    /// <summary>
    /// Wycina prostokąt w PIKSELACH klatki, z opcjonalnym padding wokół (Faza 5 cascade).
    /// Używane dla cascade mode — confirmer model dostaje bbox kandydata z proposera z paddingiem
    /// żeby miał kontekst wokół obiektu.
    /// </summary>
    /// <param name="framePath">Ścieżka do klatki źródłowej (pełna, nie crop ROI).</param>
    /// <param name="bboxX">X bbox w pikselach klatki.</param>
    /// <param name="bboxY">Y bbox w pikselach.</param>
    /// <param name="bboxW">Szerokość w pikselach.</param>
    /// <param name="bboxH">Wysokość w pikselach.</param>
    /// <param name="paddingRatio">0..1. 0.2 = 20% padding wokół bbox. Clamp do granic klatki.</param>
    /// <param name="minSize">Minimalny rozmiar cropa w pikselach (small bboxes padded do min rozmiaru). Default 128.</param>
    Task<RoiCropResult> CropBboxAsync(
        string framePath,
        int bboxX, int bboxY, int bboxW, int bboxH,
        double paddingRatio = 0.2,
        int minSize = 128,
        CancellationToken ct = default);

    void Cleanup(RoiCropResult crop);
}
