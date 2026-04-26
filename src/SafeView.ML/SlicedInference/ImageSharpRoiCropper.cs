using SafeView.Application.Abstractions.Detection;
using SafeView.Domain.Detection;

namespace SafeView.ML.SlicedInference;

/// <summary>
/// Implementacja <see cref="IRoiCropper"/> używająca ImageSharp.
/// Wrapper wokół statycznej klasy <see cref="RoiCropper"/> — osobna abstrakcja pozwala
/// mockować w testach DetectionPipeline.
/// </summary>
public sealed class ImageSharpRoiCropper : IRoiCropper
{
    public async Task<RoiCropResult> CropAsync(string framePath, RoiRectangle roi, CancellationToken ct = default)
    {
        var crop = await RoiCropper.CropAsync(framePath, roi, ct).ConfigureAwait(false);
        return new RoiCropResult(
            crop.CropPath,
            crop.OffsetX, crop.OffsetY,
            crop.CropWidth, crop.CropHeight,
            crop.OriginalWidth, crop.OriginalHeight);
    }

    public async Task<RoiCropResult> CropBboxAsync(
        string framePath,
        int bboxX, int bboxY, int bboxW, int bboxH,
        double paddingRatio = 0.2,
        int minSize = 128,
        CancellationToken ct = default)
    {
        var crop = await RoiCropper.CropBboxAsync(framePath, bboxX, bboxY, bboxW, bboxH, paddingRatio, minSize, ct)
            .ConfigureAwait(false);
        return new RoiCropResult(
            crop.CropPath,
            crop.OffsetX, crop.OffsetY,
            crop.CropWidth, crop.CropHeight,
            crop.OriginalWidth, crop.OriginalHeight);
    }

    public void Cleanup(RoiCropResult crop)
    {
        try { if (File.Exists(crop.CropPath)) File.Delete(crop.CropPath); }
        catch { /* best effort */ }
    }
}
