using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.ML;
using SafeView.Domain.Detection.Geometry;
using SafeView.Domain.ML;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SafeView.ML.SlicedInference;

/// <summary>
/// SAHI Sliced Detector — decorator wokół <see cref="IObjectDetector"/>.
///
/// Wywołuje wrapped detector dla każdego tile z <see cref="TileGrid"/>, przetrans­formowuje
/// bboxy z lokalnego układu tile do globalnego układu oryginalnego obrazu, a następnie
/// uruchamia <see cref="NonMaxSuppression"/> żeby usunąć duplikaty na granicach tiles.
///
/// Dzięki temu drobne obiekty (osoba na 50m w 4K, ~30px) są widoczne w natywnej rozdzielczości
/// — a nie skompresowane 6× przez resize do 640×640.
///
/// Koszt: ~N× inference per klatka (N = liczba tiles). Dla 1080p ROI + tile=640 → ~4 tiles.
/// Dla 4K → ~32 tiles. Optymalizacja batchingu pozostawiona na Fazę 2b (wymaga re-export
/// modelu z dynamic batch).
/// </summary>
public sealed class SlicedDetector : IObjectDetector
{
    private readonly IObjectDetector _inner;
    private readonly ILogger<SlicedDetector> _log;

    public string Backend => $"{_inner.Backend}+sliced";

    public SlicedDetector(IObjectDetector inner, ILogger<SlicedDetector> log)
    {
        _inner = inner;
        _log = log;
    }

    public async Task<DetectionResult> DetectAsync(MLModel model, string imagePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (!File.Exists(imagePath))
            return DetectionResult.Failed($"Obraz nie istnieje: {imagePath}");

        var sw = Stopwatch.StartNew();

        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct).ConfigureAwait(false);
        var tiles = TileGrid.Generate(image.Width, image.Height, model.InputSize, overlap: 0.2);

        // Jeśli obraz mieści się w jednym tile — po prostu wywołaj wrapped detector bezpośrednio
        if (tiles.Count <= 1)
        {
            return await _inner.DetectAsync(model, imagePath, ct).ConfigureAwait(false);
        }

        _log.LogDebug("Slicing {W}x{H} → {N} tiles (model={Model})", image.Width, image.Height, tiles.Count, model.Name);

        // Faza 4: Jeśli inner detector wspiera batching — używamy batched path (2-4× szybciej).
        // Inaczej iteracja sekwencyjna (fallback, ten sam wynik).
        var batchCapable = _inner is IBatchObjectDetector batchDet && batchDet.SupportsBatching(model);

        var allDetections = new List<Detection>();
        var tempDir = Path.Combine(Path.GetTempPath(), "safeview-sliced");
        Directory.CreateDirectory(tempDir);

        try
        {
            if (batchCapable && _inner is IBatchObjectDetector batched)
            {
                await RunBatchedAsync(image, tiles, model, batched, tempDir, allDetections, ct).ConfigureAwait(false);
            }
            else
            {
                await RunSequentialAsync(image, tiles, model, tempDir, allDetections, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        // Merge duplicate detections z nakładających się tiles
        var merged = MergeWithNms(allDetections, iouThreshold: model.IouThreshold);

        sw.Stop();
        _log.LogDebug("Sliced inference: {Tiles} tiles → {Raw} raw → {Merged} after NMS ({Ms}ms)",
            tiles.Count, allDetections.Count, merged.Count, sw.ElapsedMilliseconds);

        return new DetectionResult(
            Success: true,
            Detections: merged,
            SourceWidth: image.Width,
            SourceHeight: image.Height,
            Latency: sw.Elapsed);
    }

    /// <summary>Sekwencyjny fallback — każdy tile osobno.</summary>
    private async Task RunSequentialAsync(
        Image<Rgb24> image, List<Tile> tiles, MLModel model, string tempDir,
        List<Detection> allDetections, CancellationToken ct)
    {
        foreach (var tile in tiles)
        {
            ct.ThrowIfCancellationRequested();
            var tilePath = Path.Combine(tempDir, $"tile_{Guid.NewGuid():N}.jpg");
            try
            {
                using (var tileImage = image.Clone(ctx => ctx.Crop(new Rectangle(tile.X, tile.Y, tile.Width, tile.Height))))
                {
                    await tileImage.SaveAsJpegAsync(tilePath, ct).ConfigureAwait(false);
                }

                var tileResult = await _inner.DetectAsync(model, tilePath, ct).ConfigureAwait(false);
                if (!tileResult.Success) continue;

                foreach (var det in tileResult.Detections)
                {
                    allDetections.Add(new Detection(det.ClassId, det.Label, det.Confidence,
                        new BoundingBox(det.Box.X + tile.X, det.Box.Y + tile.Y, det.Box.Width, det.Box.Height)));
                }
            }
            finally
            {
                try { if (File.Exists(tilePath)) File.Delete(tilePath); } catch { }
            }
        }
    }

    /// <summary>
    /// Batched path (Faza 4) — zapisuje wszystkie tiles jako tempfiles, potem wywołuje
    /// <see cref="IBatchObjectDetector.DetectBatchAsync"/> raz dla całego batcha.
    /// </summary>
    private async Task RunBatchedAsync(
        Image<Rgb24> image, List<Tile> tiles, MLModel model, IBatchObjectDetector batched,
        string tempDir, List<Detection> allDetections, CancellationToken ct)
    {
        var tilePaths = new List<string>(tiles.Count);
        try
        {
            // Zapisz wszystkie tiles jako tempfiles (równolegle byłoby szybciej, ale pamięć → seq)
            foreach (var tile in tiles)
            {
                ct.ThrowIfCancellationRequested();
                var tilePath = Path.Combine(tempDir, $"tile_{Guid.NewGuid():N}.jpg");
                using (var tileImage = image.Clone(ctx => ctx.Crop(new Rectangle(tile.X, tile.Y, tile.Width, tile.Height))))
                {
                    await tileImage.SaveAsJpegAsync(tilePath, ct).ConfigureAwait(false);
                }
                tilePaths.Add(tilePath);
            }

            // 1× wywołanie batchowanego inference dla N tiles
            var batchResults = await batched.DetectBatchAsync(model, tilePaths, ct).ConfigureAwait(false);

            // Przetransformuj detekcje z każdego tile do układu oryginalnego obrazu
            for (int i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];
                var result = batchResults[i];
                if (!result.Success) continue;

                foreach (var det in result.Detections)
                {
                    allDetections.Add(new Detection(det.ClassId, det.Label, det.Confidence,
                        new BoundingBox(det.Box.X + tile.X, det.Box.Y + tile.Y, det.Box.Width, det.Box.Height)));
                }
            }
        }
        finally
        {
            foreach (var p in tilePaths)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }
    }

    /// <summary>Konwersja z <see cref="Detection"/> (ML namespace) do NmsCandidate + NMS + powrót.</summary>
    private static List<Detection> MergeWithNms(List<Detection> detections, double iouThreshold)
    {
        if (detections.Count <= 1) return detections;

        var candidates = new List<NonMaxSuppression.NmsCandidate>(detections.Count);
        for (int i = 0; i < detections.Count; i++)
        {
            var d = detections[i];
            candidates.Add(new NonMaxSuppression.NmsCandidate(
                d.ClassId,
                d.Confidence,
                new Bbox(d.Box.X, d.Box.Y, d.Box.Width, d.Box.Height),
                i));
        }

        var kept = NonMaxSuppression.Apply(candidates, iouThreshold);
        return kept.Select(c => detections[c.OriginalIndex]).ToList();
    }
}
