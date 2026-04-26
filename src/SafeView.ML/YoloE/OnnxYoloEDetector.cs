using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SafeView.Application.Abstractions.ML;
using SafeView.Domain.ML;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SafeView.ML.YoloE;

/// <summary>
/// <para><b>YOLOE</b> (Ultralytics, text + visual prompts) — <b>AGPL / Enterprise License</b>.
/// Implementuje <see cref="IObjectDetector"/>, <see cref="ITextPromptDetector"/>,
/// <see cref="IVisualPromptDetector"/>. Backend = "YoloE".</para>
///
/// <para><b>Swap-ready</b>: jeśli w przyszłości zastąpisz YOLOE pod inną licencją (np. OWLv2,
/// T-Rex z compatybilną licencją), nowa klasa implementuje te same interfejsy, rejestruje się
/// w DI przez <see cref="IVisualPromptDetector"/> + własny backend-name, <see cref="DetectorBackend"/>
/// dostaje nową wartość, <c>DetectorFactory</c> switch zyskuje jedną gałąź. Nic w Domain,
/// <see cref="SafeView.Domain.Detection.DetectionClass"/>, Trigger, UI nie wymaga zmian —
/// matching jest capability-driven (<see cref="ModelCapabilities.VisualPrompts"/>).</para>
///
/// <para><b>Architektura inferencji YOLOE</b>:</para>
/// <list type="number">
///   <item><c>text-encoder.onnx</c> — CLIP text encoder (reuse z YOLO-World) dla Text / TextAndVisual mode</item>
///   <item><c>image-encoder.onnx</c> — ViT-image encoder: crop referencyjny → embedding 512-dim</item>
///   <item><c>model.onnx</c> — detection head: image + concatenated (text+visual) embeddings → bboxes</item>
/// </list>
///
/// <para><b>Embeddings cache</b> (in-memory, per-class): visual refs są enkodowane raz przy
/// pierwszym użyciu klasy, potem reużywane przez restart procesu. <see cref="InvalidateClassCache"/>
/// czyści cache gdy user edytuje refs (via DetectionClassRepo.UpdateAsync albo explicit call).</para>
/// </summary>
public sealed class OnnxYoloEDetector
    : IObjectDetector, ITextPromptDetector, IVisualPromptDetector, IDisposable
{
    public string Backend => "YoloE";

    private readonly IClipTextEncoder _textEncoder;
    private readonly ILogger<OnnxYoloEDetector> _log;
    private readonly Lock _sessionLock = new();
    private readonly Dictionary<string, InferenceSession> _detectSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InferenceSession> _imageEncoderSessions = new(StringComparer.Ordinal);

    /// <summary>Cache embeddingów wizualnych per-class — key = classId, value = flat [refCount × dim].</summary>
    private readonly ConcurrentDictionary<string, float[]> _visualEmbedCache = new(StringComparer.Ordinal);

    public OnnxYoloEDetector(IClipTextEncoder textEncoder, ILogger<OnnxYoloEDetector> log)
    {
        _textEncoder = textEncoder;
        _log = log;
    }

    // ─── IObjectDetector fallback ──────────────────────────────────────────

    public async Task<DetectionResult> DetectAsync(MLModel model, string imagePath, CancellationToken ct = default)
    {
        // Fallback: użyj model.Labels jako text prompts (zachowanie identyczne z YoloWorld).
        if (model.Labels.Count == 0)
            return DetectionResult.Failed("YOLOE: model nie ma Labels; użyj DetectWithPromptsAsync lub DetectWithVisualPromptsAsync.");
        return await DetectWithPromptsAsync(model, imagePath, model.Labels, ct).ConfigureAwait(false);
    }

    // ─── ITextPromptDetector ───────────────────────────────────────────────

    public bool SupportsDynamicPrompts(MLModel model)
    {
        try
        {
            var detectPath = ResolveDetectOnnxPath(model);
            if (detectPath is null || !File.Exists(detectPath)) return false;
            return model.Capabilities.HasFlag(ModelCapabilities.TextPrompts);
        }
        catch { return false; }
    }

    public async Task<DetectionResult> DetectWithPromptsAsync(
        MLModel model, string imagePath, IReadOnlyList<string> prompts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (prompts.Count == 0) return DetectionResult.Failed("YOLOE: lista promptów pusta.");

        var textEmbeds = await _textEncoder.EncodeAsync(prompts, ct).ConfigureAwait(false);
        return await RunInferenceAsync(model, imagePath, textEmbeds, _textEncoder.EmbeddingDimension, prompts, ct)
            .ConfigureAwait(false);
    }

    // ─── IVisualPromptDetector ─────────────────────────────────────────────

    public bool SupportsVisualPrompts(MLModel model)
    {
        try
        {
            var imgEncPath = ResolveImageEncoderPath(model);
            if (imgEncPath is null || !File.Exists(imgEncPath)) return false;
            return model.Capabilities.HasFlag(ModelCapabilities.VisualPrompts);
        }
        catch { return false; }
    }

    public async Task<DetectionResult> DetectWithVisualPromptsAsync(
        MLModel model, string imagePath, IReadOnlyList<VisualPromptClass> visualClasses, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (visualClasses.Count == 0)
            return DetectionResult.Failed("YOLOE: lista visual classes pusta.");

        if (!SupportsVisualPrompts(model))
            return DetectionResult.Failed(
                $"Model '{model.Name}' nie obsługuje visual-prompts (brak image-encoder.onnx lub kapabilitetu).");

        // Dla każdej klasy: pobierz embedding (cache-first, encoder-fallback). Wszystkie embeddings
        // jednej klasy uśredniamy do pojedynczego wektora przed feed do detection head.
        var dim = _textEncoder.EmbeddingDimension; // YOLOE text/visual spaces są zharmonizowane do tego samego dim
        var classEmbeds = new float[visualClasses.Count * dim];
        var labels = new List<string>(visualClasses.Count);

        for (int i = 0; i < visualClasses.Count; i++)
        {
            var cls = visualClasses[i];
            labels.Add(cls.Label);

            var classEmbed = await GetOrComputeClassEmbeddingAsync(model, cls, ct).ConfigureAwait(false);
            if (classEmbed.Length != dim)
                return DetectionResult.Failed(
                    $"Visual embedding dla klasy '{cls.Label}' ma dim={classEmbed.Length}, oczekiwano {dim}.");

            Buffer.BlockCopy(classEmbed, 0, classEmbeds, i * dim * sizeof(float), dim * sizeof(float));
        }

        return await RunInferenceAsync(model, imagePath, classEmbeds, dim, labels, ct).ConfigureAwait(false);
    }

    public void InvalidateClassCache(string classId)
    {
        if (_visualEmbedCache.TryRemove(classId, out _))
            _log.LogDebug("YOLOE: visual cache invalidated for class {Id}", classId);
    }

    // ─── Visual embeddings (cache + compute) ───────────────────────────────

    /// <summary>
    /// Zwraca uśredniony embedding wizualny dla klasy (wszystkie refs → mean). Cache-first
    /// (per-class w pamięci), miss → liczy przez image encoder ONNX dla każdego ref-a i średnia.
    /// </summary>
    private async Task<float[]> GetOrComputeClassEmbeddingAsync(
        MLModel model, VisualPromptClass cls, CancellationToken ct)
    {
        if (_visualEmbedCache.TryGetValue(cls.ClassId, out var cached)) return cached;

        var imgEncPath = ResolveImageEncoderPath(model)
            ?? throw new InvalidOperationException("image-encoder.onnx nie znaleziony obok YOLOE model.onnx.");
        var session = GetOrCreateImageEncoderSession(model.Id, imgEncPath);

        var dim = _textEncoder.EmbeddingDimension;
        var sum = new float[dim];
        int count = 0;

        foreach (var refPath in cls.ReferenceImagePaths)
        {
            if (!File.Exists(refPath))
            {
                _log.LogWarning("YOLOE visual ref nie istnieje: {Path}", refPath);
                continue;
            }
            var embed = await EncodeImageAsync(session, refPath, ct).ConfigureAwait(false);
            if (embed.Length != dim)
            {
                _log.LogWarning("YOLOE image encoder zwrócił nieoczekiwany dim {D} (oczekiwano {Exp}) dla {Path}",
                    embed.Length, dim, refPath);
                continue;
            }
            for (int i = 0; i < dim; i++) sum[i] += embed[i];
            count++;
        }

        if (count == 0)
            throw new InvalidOperationException($"Żaden ref klasy '{cls.Label}' nie dał embeddingu.");

        // Mean + L2-normalize
        for (int i = 0; i < dim; i++) sum[i] /= count;
        double sqSum = 0;
        for (int i = 0; i < dim; i++) sqSum += sum[i] * sum[i];
        var norm = (float)Math.Sqrt(sqSum);
        if (norm > 1e-6f)
        {
            var inv = 1f / norm;
            for (int i = 0; i < dim; i++) sum[i] *= inv;
        }

        _visualEmbedCache[cls.ClassId] = sum;
        return sum;
    }

    /// <summary>
    /// Ładuje obraz i enkoduje przez YOLOE image encoder. Preprocessing ViT-style: resize do
    /// 224×224 (CLIP-B32 compat), CHW, normalization ImageNet mean/std.
    /// </summary>
    private static async Task<float[]> EncodeImageAsync(InferenceSession session, string imagePath, CancellationToken ct)
    {
        const int InputSize = 224;
        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct).ConfigureAwait(false);
        using var resized = image.Clone(ctx => ctx.Resize(InputSize, InputSize));

        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        // ImageNet mean/std (CLIP ViT pretraining)
        float[] mean = [0.48145466f, 0.4578275f, 0.40821073f];
        float[] std = [0.26862954f, 0.26130258f, 0.27577711f];

        resized.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < InputSize; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < InputSize; x++)
                {
                    var p = row[x];
                    tensor[0, 0, y, x] = (p.R / 255f - mean[0]) / std[0];
                    tensor[0, 1, y, x] = (p.G / 255f - mean[1]) / std[1];
                    tensor[0, 2, y, x] = (p.B / 255f - mean[2]) / std[2];
                }
            }
        });

        var inputName = session.InputMetadata.Keys.First();
        using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        var output = outputs[0].AsTensor<float>();
        var dims = output.Dimensions;
        // Expected [1, D] albo [1, D, 1, 1] po pooling
        int d = dims.Length >= 2 ? dims[1] : (int)output.Length;
        var result = new float[d];
        for (int i = 0; i < d; i++) result[i] = output.GetValue(i);
        return result;
    }

    // ─── Detection inference (shared dla text / visual / combined) ─────────

    /// <summary>
    /// Uruchamia główny detection head YOLOE z podanymi class embeddings (text / visual / mix).
    /// Layout inputs jest taki sam jak w YOLO-World (image + class_embeds), więc postprocess
    /// jest współdzielony.
    /// </summary>
    private async Task<DetectionResult> RunInferenceAsync(
        MLModel model, string imagePath,
        float[] classEmbeddings, int embedDim,
        IReadOnlyList<string> labels,
        CancellationToken ct)
    {
        var absPath = ResolveDetectOnnxPath(model);
        if (absPath is null) return DetectionResult.Failed("YOLOE: OnnxPath nieustawiony.");
        if (!File.Exists(absPath)) return DetectionResult.Failed($"YOLOE: plik modelu nie istnieje: {absPath}");

        var session = GetOrCreateDetectSession(model.Id, absPath);
        var sw = Stopwatch.StartNew();

        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct).ConfigureAwait(false);
        int srcW = image.Width, srcH = image.Height;

        var (imageTensor, scale, padX, padY) = LetterboxPreprocess(image, model.InputSize);
        var inputs = new List<NamedOnnxValue>(2);

        var imageInputName = session.InputMetadata.Keys.First();
        inputs.Add(NamedOnnxValue.CreateFromTensor(imageInputName, imageTensor));

        if (session.InputMetadata.Count >= 2)
        {
            var textTensor = new DenseTensor<float>([1, labels.Count, embedDim]);
            for (int i = 0; i < labels.Count; i++)
                for (int j = 0; j < embedDim; j++)
                    textTensor[0, i, j] = classEmbeddings[i * embedDim + j];

            var textInputName = session.InputMetadata.Keys.Skip(1).First();
            inputs.Add(NamedOnnxValue.CreateFromTensor(textInputName, textTensor));
        }

        using var outputs = session.Run(inputs);
        var output = outputs[0].AsTensor<float>();

        var detections = Postprocess(output, labels, model, scale, padX, padY, srcW, srcH);
        sw.Stop();
        return new DetectionResult(true, detections, srcW, srcH, sw.Elapsed);
    }

    // ─── Session + path resolution ─────────────────────────────────────────

    private InferenceSession GetOrCreateDetectSession(string modelId, string absPath)
    {
        lock (_sessionLock)
        {
            if (_detectSessions.TryGetValue(modelId, out var s)) return s;
            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            var session = new InferenceSession(absPath, opts);
            _detectSessions[modelId] = session;
            _log.LogInformation("YOLOE detect session loaded for {Id} from {Path}", modelId, absPath);
            return session;
        }
    }

    private InferenceSession GetOrCreateImageEncoderSession(string modelId, string absPath)
    {
        lock (_sessionLock)
        {
            if (_imageEncoderSessions.TryGetValue(modelId, out var s)) return s;
            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            var session = new InferenceSession(absPath, opts);
            _imageEncoderSessions[modelId] = session;
            _log.LogInformation("YOLOE image-encoder session loaded for {Id} from {Path}", modelId, absPath);
            return session;
        }
    }

    private static string? ResolveDetectOnnxPath(MLModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.OnnxAbsolutePath)) return model.OnnxAbsolutePath;
        return model.OnnxRelativePath;
    }

    /// <summary>
    /// image-encoder.onnx jest bundled obok model.onnx w runtime/models/{name}/. Lokalizacja
    /// relatywna do detect model path.
    /// </summary>
    private static string? ResolveImageEncoderPath(MLModel model)
    {
        var detectPath = ResolveDetectOnnxPath(model);
        if (string.IsNullOrEmpty(detectPath)) return null;
        var dir = Path.GetDirectoryName(detectPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var candidate = Path.Combine(dir, "image-encoder.onnx");
        return File.Exists(candidate) ? candidate : null;
    }

    // ─── Pre / post (YOLOv8-style, wspólne z YOLO-World) ───────────────────

    private static (DenseTensor<float> Tensor, float Scale, int PadX, int PadY)
        LetterboxPreprocess(Image<Rgb24> src, int size)
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
        Tensor<float> output, IReadOnlyList<string> labels, MLModel model,
        float scale, int padX, int padY, int srcW, int srcH)
    {
        var dims = output.Dimensions.ToArray();
        if (dims.Length != 3 || dims[0] != 1) return [];

        int nc = labels.Count;
        bool v8Layout = dims[1] == 4 + nc || (dims[1] < dims[2] && dims[1] >= 4);
        int numBoxes = v8Layout ? dims[2] : dims[1];
        float confTh = (float)model.ConfidenceThreshold;

        var raw = new List<(float x, float y, float w, float h, float conf, int cls)>(128);
        for (int i = 0; i < numBoxes; i++)
        {
            float cx, cy, w, h, bestConf = 0f;
            int bestCls = -1;

            if (v8Layout)
            {
                cx = output[0, 0, i]; cy = output[0, 1, i];
                w  = output[0, 2, i]; h  = output[0, 3, i];
                for (int c = 0; c < nc; c++)
                {
                    var s = output[0, 4 + c, i];
                    if (s > bestConf) { bestConf = s; bestCls = c; }
                }
            }
            else
            {
                cx = output[0, i, 0]; cy = output[0, i, 1];
                w  = output[0, i, 2]; h  = output[0, i, 3];
                for (int c = 0; c < nc; c++)
                {
                    var s = output[0, i, 4 + c];
                    if (s > bestConf) { bestConf = s; bestCls = c; }
                }
            }
            if (bestCls < 0 || bestConf < confTh) continue;
            raw.Add((cx, cy, w, h, bestConf, bestCls));
        }

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

                string label = s.cls < labels.Count ? labels[s.cls] : $"class_{s.cls}";
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
            foreach (var s in _detectSessions.Values) s.Dispose();
            foreach (var s in _imageEncoderSessions.Values) s.Dispose();
            _detectSessions.Clear();
            _imageEncoderSessions.Clear();
            _visualEmbedCache.Clear();
        }
    }
}
