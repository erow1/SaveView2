using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SafeView.Application.Abstractions.ML;
using SafeView.Application.Abstractions.Storage;
using SafeView.Application.Configuration;
using SafeView.Domain.ML;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SafeView.ML;

/// <summary>
/// Detektor oparty o ONNX Runtime. Dostosowany do modeli YOLO-style
/// (YOLOv5 / YOLOv8 / YOLOv9 — output ma shape [1, 4+nc, N] lub [1, N, 4+nc]).
///
/// Preprocessing:
///   letterbox → resize do InputSize×InputSize → RGB float [0,1] CHW → tensor [1,3,H,W]
/// Postprocessing:
///   reshape → filter przez confidence → convert xywh → xyxy → NMS → rescale do oryginalnych px.
///
/// Sesja ONNX jest cachowana per-model (thread-safe).
/// </summary>
public sealed class OnnxObjectDetector : IObjectDetector, IBatchObjectDetector, IDisposable
{
    private readonly IFileStore _files;
    private readonly ILogger<OnnxObjectDetector> _log;
    private readonly IOptionsMonitor<MLOptions>? _mlOpts;
    private readonly Lock _sessionLock = new();
    private readonly Dictionary<string, InferenceSession> _sessions = new(StringComparer.Ordinal);

    public string Backend => "onnx";

    public OnnxObjectDetector(IFileStore files, ILogger<OnnxObjectDetector> log, IOptionsMonitor<MLOptions>? mlOpts = null)
    {
        _files = files;
        _log = log;
        _mlOpts = mlOpts; // optional — brak rejestracji = CPU fallback
    }

    public async Task<DetectionResult> DetectAsync(MLModel model, string imagePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (model.Backend != DetectorBackend.Onnx)
            return DetectionResult.Failed($"Model {model.Name} nie jest ONNX.");

        // Preferuj OnnxAbsolutePath (bundled modele z runtime/models/),
        // fallback na OnnxRelativePath (user-upload w storage/models/).
        string abs;
        if (!string.IsNullOrWhiteSpace(model.OnnxAbsolutePath))
        {
            abs = model.OnnxAbsolutePath;
        }
        else if (!string.IsNullOrWhiteSpace(model.OnnxRelativePath))
        {
            abs = _files.ResolveAbsolutePath(FileKind.Model, model.OnnxRelativePath);
        }
        else
        {
            return DetectionResult.Failed("Ani OnnxAbsolutePath, ani OnnxRelativePath nie są ustawione.");
        }

        if (!File.Exists(abs))
            return DetectionResult.Failed($"Plik modelu nie istnieje: {abs}");

        InferenceSession session;
        try { session = GetOrCreateSession(model.Id, abs); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load ONNX session for {Model} at {Path}", model.Name, abs);
            return DetectionResult.Failed($"Nie udało się wczytać modelu: {ex.Message}");
        }

        var sw = Stopwatch.StartNew();

        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct).ConfigureAwait(false);
        int srcW = image.Width, srcH = image.Height;

        // Letterbox → square
        var (tensor, scale, padX, padY) = Preprocess(image, model.InputSize);

        var inputName = session.InputMetadata.Keys.First();
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

        using var outputs = session.Run(inputs);
        var output = outputs[0].AsTensor<float>();

        var detections = Postprocess(output, model, scale, padX, padY, srcW, srcH);

        sw.Stop();
        return new DetectionResult(true, detections, srcW, srcH, sw.Elapsed);
    }

    private InferenceSession GetOrCreateSession(string modelId, string absPath)
    {
        lock (_sessionLock)
        {
            if (_sessions.TryGetValue(modelId, out var s)) return s;

            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            var mlCfg = _mlOpts?.CurrentValue;
            var providerUsed = "cpu";

            // Ticket #2 — GPU acceleration:
            // 1. Gdy UseGpu=true → spróbuj CUDA (dostępne tylko gdy Microsoft.ML.OnnxRuntime.Gpu jest referenced)
            // 2. Gdy CUDA niedostępne → spróbuj FallbackProvider (DirectML/CoreML)
            // 3. Gdy nic nie działa → CPU (default ORT path)
            if (mlCfg is { UseGpu: true })
            {
                try
                {
                    opts.AppendExecutionProvider_CUDA(mlCfg.GpuDeviceId);
                    providerUsed = $"cuda:{mlCfg.GpuDeviceId}";
                }
                catch (Exception ex)
                {
                    // GPU provider niedostępny → Warning + fallback
                    _log.LogWarning(ex, "CUDA provider unavailable for model {Id} — falling back.", modelId);
                    TryAppendFallback(opts, mlCfg.FallbackProvider, ref providerUsed);
                }
            }
            else if (mlCfg is not null && !string.IsNullOrWhiteSpace(mlCfg.FallbackProvider))
            {
                TryAppendFallback(opts, mlCfg.FallbackProvider, ref providerUsed);
            }

            var session = new InferenceSession(absPath, opts);
            _sessions[modelId] = session;
            _log.LogInformation("Loaded ONNX session for model {Id} from {Path} (provider: {Provider})",
                modelId, absPath, providerUsed);
            return session;
        }
    }

    /// <summary>Próbuje dodać alternatywny provider (DirectML/CoreML) — swallow exception na fallback.</summary>
    private void TryAppendFallback(SessionOptions opts, string? provider, ref string used)
    {
        if (string.IsNullOrWhiteSpace(provider)) return;
        try
        {
            switch (provider.ToLowerInvariant())
            {
                case "directml":
                    opts.AppendExecutionProvider_DML(0);
                    used = "directml:0";
                    break;
                case "coreml":
                    opts.AppendExecutionProvider_CoreML();
                    used = "coreml";
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Fallback provider '{Provider}' unavailable — using CPU.", provider);
        }
    }

    // ── IBatchObjectDetector (Faza 4 — batching SAHI tiles w jednym Run()) ────

    /// <summary>
    /// Sprawdza czy input modelu ma dynamic batch dim (dim[0] == -1 lub = nazwany symbol).
    /// Modele eksportowane z <c>yolo export ... dynamic=True</c> mają to ustawione.
    /// </summary>
    public bool SupportsBatching(MLModel model)
    {
        try
        {
            var abs = ResolveOnnxPath(model);
            if (abs is null) return false;
            var session = GetOrCreateSession(model.Id, abs);
            var inputDims = session.InputMetadata.Values.First().Dimensions;
            // Dynamic dim jest reprezentowane jako -1 w ONNX Runtime metadata
            return inputDims.Length > 0 && inputDims[0] <= 0;
        }
        catch { return false; }
    }

    public async Task<DetectionResult[]> DetectBatchAsync(
        MLModel model, IReadOnlyList<string> imagePaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count == 0) return [];

        // Fallback: model nie wspiera batchingu → iteracja sekwencyjna (wciąż poprawny wynik).
        if (!SupportsBatching(model))
        {
            var results = new DetectionResult[imagePaths.Count];
            for (int i = 0; i < imagePaths.Count; i++)
                results[i] = await DetectAsync(model, imagePaths[i], ct).ConfigureAwait(false);
            return results;
        }

        var abs = ResolveOnnxPath(model);
        if (abs is null)
            return Enumerable.Repeat(DetectionResult.Failed("Model path not resolved"), imagePaths.Count).ToArray();

        var session = GetOrCreateSession(model.Id, abs);
        var sw = Stopwatch.StartNew();

        // Preprocess N obrazów → stacked tensor [N, 3, H, W]
        var n = imagePaths.Count;
        var size = model.InputSize;
        var batchTensor = new DenseTensor<float>([n, 3, size, size]);
        var scales = new float[n];
        var padXs = new int[n];
        var padYs = new int[n];
        var srcSizes = new (int W, int H)[n];

        for (int k = 0; k < n; k++)
        {
            using var image = await Image.LoadAsync<Rgb24>(imagePaths[k], ct).ConfigureAwait(false);
            srcSizes[k] = (image.Width, image.Height);
            var (scale, padX, padY) = StackLetterbox(image, size, batchTensor, k);
            scales[k] = scale; padXs[k] = padX; padYs[k] = padY;
        }

        var inputName = session.InputMetadata.Keys.First();
        using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(inputName, batchTensor)]);
        var output = outputs[0].AsTensor<float>();
        sw.Stop();

        // Postprocess — output ma kształt [N, 4+nc, boxes] lub [N, boxes, 5+nc]
        // Splittujemy output per-batch-index i reużywamy postprocess logic.
        var results2 = new DetectionResult[n];
        for (int k = 0; k < n; k++)
        {
            var dets = PostprocessBatchItem(output, k, model, scales[k], padXs[k], padYs[k], srcSizes[k].W, srcSizes[k].H);
            results2[k] = new DetectionResult(true, dets, srcSizes[k].W, srcSizes[k].H,
                // Latency rozkładamy równomiernie — całe Run() wykonało się raz dla N obrazów
                TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds / (double)n));
        }

        _log.LogDebug("Batched inference: {N} images in {Ms}ms ({PerMs}ms/image)",
            n, sw.ElapsedMilliseconds, sw.ElapsedMilliseconds / (double)n);

        return results2;
    }

    /// <summary>
    /// Preprocess jednego obrazu + zapis do slotu <paramref name="batchIdx"/> w <paramref name="batchTensor"/>.
    /// Wariant <see cref="Preprocess"/> który zapisuje bezpośrednio do shared batched tensora.
    /// </summary>
    private static (float Scale, int PadX, int PadY) StackLetterbox(
        Image<Rgb24> src, int size, DenseTensor<float> batchTensor, int batchIdx)
    {
        var scale = Math.Min((float)size / src.Width, (float)size / src.Height);
        var newW = (int)Math.Round(src.Width * scale);
        var newH = (int)Math.Round(src.Height * scale);
        var padX = (size - newW) / 2;
        var padY = (size - newH) / 2;

        using var resized = src.Clone(ctx => ctx.Resize(newW, newH));
        using var canvas = new Image<Rgb24>(size, size, new Rgb24(114, 114, 114));
        canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(padX, padY), 1f));

        canvas.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < size; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < size; x++)
                {
                    var p = row[x];
                    batchTensor[batchIdx, 0, y, x] = p.R / 255f;
                    batchTensor[batchIdx, 1, y, x] = p.G / 255f;
                    batchTensor[batchIdx, 2, y, x] = p.B / 255f;
                }
            }
        });

        return (scale, padX, padY);
    }

    /// <summary>Postprocess jednego elementu z batched output.</summary>
    private static List<Detection> PostprocessBatchItem(
        Tensor<float> output, int batchIdx, MLModel model,
        float scale, int padX, int padY, int srcW, int srcH)
    {
        // Output jest [N, 4+nc, boxes] lub [N, boxes, 5+nc]. Wyciągamy slice dla batchIdx
        // przez ręczne indexy — żeby nie alokować nowego tensora, tworzymy wrapper.
        var dims = output.Dimensions.ToArray();
        if (dims.Length != 3 || batchIdx >= dims[0]) return [];

        int nc = Math.Max(1, model.Labels.Count);
        bool v8Layout = dims[1] == 4 + nc || (dims[1] < dims[2] && dims[1] >= 4);
        int numBoxes = v8Layout ? dims[2] : dims[1];
        int stride = v8Layout ? dims[1] : dims[2];

        float confTh = (float)model.ConfidenceThreshold;
        var raw = new List<(float x, float y, float w, float h, float conf, int cls)>(128);

        for (int i = 0; i < numBoxes; i++)
        {
            float cx, cy, w, h, bestConf = 0f;
            int bestCls = -1;

            if (v8Layout)
            {
                cx = output[batchIdx, 0, i];
                cy = output[batchIdx, 1, i];
                w = output[batchIdx, 2, i];
                h = output[batchIdx, 3, i];
                for (int c = 0; c < nc; c++)
                {
                    var s = output[batchIdx, 4 + c, i];
                    if (s > bestConf) { bestConf = s; bestCls = c; }
                }
            }
            else
            {
                cx = output[batchIdx, i, 0];
                cy = output[batchIdx, i, 1];
                w = output[batchIdx, i, 2];
                h = output[batchIdx, i, 3];
                float obj = stride > 4 + nc ? output[batchIdx, i, 4] : 1f;
                int offset = stride > 4 + nc ? 5 : 4;
                for (int c = 0; c < nc; c++)
                {
                    var s = output[batchIdx, i, offset + c] * obj;
                    if (s > bestConf) { bestConf = s; bestCls = c; }
                }
            }

            if (bestCls < 0 || bestConf < confTh) continue;
            raw.Add((cx, cy, w, h, bestConf, bestCls));
        }

        return ApplyNmsAndScale(raw, model, scale, padX, padY, srcW, srcH);
    }

    /// <summary>Helper — NMS + rescale do oryginalnych pikseli. Extracted z <see cref="Postprocess"/>.</summary>
    private static List<Detection> ApplyNmsAndScale(
        List<(float x, float y, float w, float h, float conf, int cls)> raw,
        MLModel model, float scale, int padX, int padY, int srcW, int srcH)
    {
        var kept = new List<Detection>();
        foreach (var group in raw.GroupBy(r => r.cls))
        {
            var sorted = group.OrderByDescending(r => r.conf).ToList();
            var survivors = new List<(float x, float y, float w, float h, float conf, int cls)>();
            foreach (var cand in sorted)
            {
                bool suppressed = false;
                foreach (var s in survivors)
                    if (IoU(cand, s) > model.IouThreshold) { suppressed = true; break; }
                if (!suppressed) survivors.Add(cand);
            }
            foreach (var s in survivors)
            {
                float x1 = (s.x - s.w / 2 - padX) / scale;
                float y1 = (s.y - s.h / 2 - padY) / scale;
                float x2 = (s.x + s.w / 2 - padX) / scale;
                float y2 = (s.y + s.h / 2 - padY) / scale;
                x1 = Math.Clamp(x1, 0, srcW); y1 = Math.Clamp(y1, 0, srcH);
                x2 = Math.Clamp(x2, 0, srcW); y2 = Math.Clamp(y2, 0, srcH);
                var label = s.cls < model.Labels.Count ? model.Labels[s.cls] : $"class_{s.cls}";
                kept.Add(new Detection(s.cls, label, s.conf, new BoundingBox(x1, y1, x2 - x1, y2 - y1)));
            }
        }
        return kept;
    }

    /// <summary>Resolve absolute ONNX path (same logic as DetectAsync).</summary>
    private string? ResolveOnnxPath(MLModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.OnnxAbsolutePath))
            return model.OnnxAbsolutePath;
        if (!string.IsNullOrWhiteSpace(model.OnnxRelativePath))
            return _files.ResolveAbsolutePath(FileKind.Model, model.OnnxRelativePath);
        return null;
    }

    // ── Pre/post ────────────────────────────────────────────────────────────────

    private static (DenseTensor<float> Tensor, float Scale, int PadX, int PadY) Preprocess(Image<Rgb24> src, int size)
    {
        var scale = Math.Min((float)size / src.Width, (float)size / src.Height);
        var newW = (int)Math.Round(src.Width * scale);
        var newH = (int)Math.Round(src.Height * scale);
        var padX = (size - newW) / 2;
        var padY = (size - newH) / 2;

        using var resized = src.Clone(ctx => ctx.Resize(newW, newH));
        using var canvas = new Image<Rgb24>(size, size, new Rgb24(114, 114, 114));
        canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(padX, padY), 1f));

        var tensor = new DenseTensor<float>([1, 3, size, size]);
        canvas.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < size; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < size; x++)
                {
                    var p = row[x];
                    tensor[0, 0, y, x] = p.R / 255f;
                    tensor[0, 1, y, x] = p.G / 255f;
                    tensor[0, 2, y, x] = p.B / 255f;
                }
            }
        });

        return (tensor, scale, padX, padY);
    }

    private static List<Detection> Postprocess(
        Tensor<float> output, MLModel model, float scale, int padX, int padY, int srcW, int srcH)
    {
        // YOLOv8 output: [1, 4+nc, N]; YOLOv5: [1, N, 5+nc]. Rozpoznaj po kształcie.
        var dims = output.Dimensions.ToArray();
        if (dims.Length != 3 || dims[0] != 1) return [];

        int nc = Math.Max(1, model.Labels.Count);
        bool v8Layout = dims[1] == 4 + nc || (dims[1] < dims[2] && dims[1] >= 4);
        int numBoxes = v8Layout ? dims[2] : dims[1];
        int stride = v8Layout ? dims[1] : dims[2];

        float confTh = (float)model.ConfidenceThreshold;
        var raw = new List<(float x, float y, float w, float h, float conf, int cls)>(capacity: 128);

        for (int i = 0; i < numBoxes; i++)
        {
            float cx, cy, w, h, bestConf = 0f;
            int bestCls = -1;

            if (v8Layout)
            {
                cx = output[0, 0, i];
                cy = output[0, 1, i];
                w  = output[0, 2, i];
                h  = output[0, 3, i];
                for (int c = 0; c < nc; c++)
                {
                    var s = output[0, 4 + c, i];
                    if (s > bestConf) { bestConf = s; bestCls = c; }
                }
            }
            else
            {
                cx = output[0, i, 0];
                cy = output[0, i, 1];
                w  = output[0, i, 2];
                h  = output[0, i, 3];
                float obj = stride > 4 + nc ? output[0, i, 4] : 1f;
                int offset = stride > 4 + nc ? 5 : 4;
                for (int c = 0; c < nc; c++)
                {
                    var s = output[0, i, offset + c] * obj;
                    if (s > bestConf) { bestConf = s; bestCls = c; }
                }
            }

            if (bestCls < 0 || bestConf < confTh) continue;
            raw.Add((cx, cy, w, h, bestConf, bestCls));
        }

        // NMS per-class
        var kept = new List<Detection>();
        foreach (var group in raw.GroupBy(r => r.cls))
        {
            var sorted = group.OrderByDescending(r => r.conf).ToList();
            var survivors = new List<(float x, float y, float w, float h, float conf, int cls)>();
            foreach (var cand in sorted)
            {
                bool suppressed = false;
                foreach (var s in survivors)
                {
                    if (IoU(cand, s) > model.IouThreshold) { suppressed = true; break; }
                }
                if (!suppressed) survivors.Add(cand);
            }

            foreach (var s in survivors)
            {
                // xywh (letterboxed) → xyxy → oryginalne px
                float x1 = (s.x - s.w / 2 - padX) / scale;
                float y1 = (s.y - s.h / 2 - padY) / scale;
                float x2 = (s.x + s.w / 2 - padX) / scale;
                float y2 = (s.y + s.h / 2 - padY) / scale;
                x1 = Math.Clamp(x1, 0, srcW); y1 = Math.Clamp(y1, 0, srcH);
                x2 = Math.Clamp(x2, 0, srcW); y2 = Math.Clamp(y2, 0, srcH);

                var label = s.cls < model.Labels.Count ? model.Labels[s.cls] : $"class_{s.cls}";
                kept.Add(new Detection(s.cls, label, s.conf, new BoundingBox(x1, y1, x2 - x1, y2 - y1)));
            }
        }

        return kept;
    }

    private static float IoU(
        (float x, float y, float w, float h, float conf, int cls) a,
        (float x, float y, float w, float h, float conf, int cls) b)
    {
        float ax1 = a.x - a.w / 2, ay1 = a.y - a.h / 2, ax2 = a.x + a.w / 2, ay2 = a.y + a.h / 2;
        float bx1 = b.x - b.w / 2, by1 = b.y - b.h / 2, bx2 = b.x + b.w / 2, by2 = b.y + b.h / 2;
        float ix = Math.Max(0, Math.Min(ax2, bx2) - Math.Max(ax1, bx1));
        float iy = Math.Max(0, Math.Min(ay2, by2) - Math.Max(ay1, by1));
        float inter = ix * iy;
        float union = a.w * a.h + b.w * b.h - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }
}
