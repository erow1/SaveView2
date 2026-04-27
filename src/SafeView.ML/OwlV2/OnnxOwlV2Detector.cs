using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SafeView.Application.Abstractions.ML;
using SafeView.Domain.ML;
using SafeView.ML.YoloWorld;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SafeView.ML.OwlV2;

/// <summary>
/// <para>OWLv2 (Google, Apache 2.0) — open-vocabulary object detector based on Vision Transformer.
/// Implementuje <see cref="IObjectDetector"/> + <see cref="ITextPromptDetector"/>.</para>
///
/// <para>Architektura (single fused ONNX z <c>onnx-community/owlv2-base-patch16-ensemble-ONNX</c>):</para>
/// <list type="bullet">
///   <item><b>3 inputs</b>: <c>pixel_values [B,3,960,960]</c> (CLIP-normalized RGB),
///         <c>input_ids [N,16]</c> (tokenized prompts), <c>attention_mask [N,16]</c></item>
///   <item><b>4 outputs</b>: <c>logits [B,3600,N]</c> (raw → sigmoid → scores per anchor per query),
///         <c>pred_boxes [B,3600,4]</c> (cxywh normalized [0,1] względem padded 960×960),
///         <c>text_embeds</c>, <c>image_embeds</c> (ignorujemy)</item>
/// </list>
///
/// <para><b>Preprocessing</b>: letterbox to 960×960 (gray pad #7F7F7F), rescale /255,
/// CLIP normalize per-channel (mean=[0.481, 0.458, 0.408], std=[0.269, 0.261, 0.276]).</para>
///
/// <para><b>Postprocessing</b>: sigmoid logits → scores; pred_boxes są cxywh w [0,1] padded space →
/// denormalize do 960-space → letterbox-undo do oryginału. Per-class NMS na xyxy.</para>
///
/// <para><b>Vs YOLO-World</b>: znacznie szersze pokrycie klas (Google trenowany na DRAGON dataset
/// + visual genome). Lepiej radzi sobie z częściami ciała ('face', 'hand'), szczegółami odzieży
/// ('pants', 'shoe') i rzadkimi obiektami. Wolniejszy (~3-5× CPU) bo 960² + ViT.</para>
/// </summary>
public sealed class OnnxOwlV2Detector : IObjectDetector, ITextPromptDetector, IDisposable
{
    public string Backend => "OwlV2";

    /// <summary>
    /// Domyślny rozmiar wejścia gdy <see cref="MLModel.InputSize"/> nie jest ustawiony.
    /// OWLv2 base patch16 = 960; OWLv2 large patch14 = 1008. Per-model wartość czytana
    /// z preprocessor_config.json przez <c>ModelSeeder</c>.
    /// </summary>
    private const int DefaultInputSize = 960;

    /// <summary>OWLv2 tokenizer config — short prompts dla detection (krótsze niż CLIP 77).</summary>
    private const int OwlV2MaxTokens = 16;

    // CLIP image preprocessing constants (per OWLv2 preprocessor_config.json)
    private static readonly float[] CLIP_MEAN = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] CLIP_STD = [0.26862954f, 0.26130258f, 0.27577711f];
    private const byte LetterboxPadGray = 127;

    private readonly ClipTokenizer _tokenizer;
    private readonly ILogger<OnnxOwlV2Detector> _log;
    private readonly Lock _sessionLock = new();
    private readonly Dictionary<string, InferenceSession> _sessions = new(StringComparer.Ordinal);

    public OnnxOwlV2Detector(ClipTokenizer tokenizer, ILogger<OnnxOwlV2Detector> log)
    {
        _tokenizer = tokenizer;
        _log = log;
    }

    // ─── IObjectDetector ───────────────────────────────────────────────────

    public async Task<DetectionResult> DetectAsync(MLModel model, string imagePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Labels.Count == 0)
            return DetectionResult.Failed(
                "OWLv2: model nie ma Labels ani nie dostarczono promptów. " +
                "Dodaj Labels w /models albo użyj DetectWithPromptsAsync.");
        return await DetectWithPromptsAsync(model, imagePath, model.Labels, ct).ConfigureAwait(false);
    }

    // ─── ITextPromptDetector ───────────────────────────────────────────────

    public bool SupportsDynamicPrompts(MLModel model) => true;

    public async Task<DetectionResult> DetectWithPromptsAsync(
        MLModel model, string imagePath, IReadOnlyList<string> prompts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(prompts);
        if (prompts.Count == 0)
            return DetectionResult.Failed("OWLv2: lista promptów jest pusta.");

        var absPath = ResolveOnnxPath(model);
        if (absPath is null)
            return DetectionResult.Failed("OWLv2: nie ustawiono OnnxAbsolutePath/RelativePath.");
        if (!File.Exists(absPath))
            return DetectionResult.Failed($"OWLv2: plik modelu nie istnieje: {absPath}");

        InferenceSession session;
        try { session = GetOrCreateSession(model.Id, absPath); }
        catch (Exception ex)
        {
            _log.LogError(ex, "OWLv2: nie udało się wczytać sesji ONNX dla {Model}", model.Name);
            return DetectionResult.Failed($"Nie udało się wczytać modelu: {ex.Message}");
        }

        var sw = Stopwatch.StartNew();

        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct).ConfigureAwait(false);
        int srcW = image.Width, srcH = image.Height;

        // 1. Image preprocessing: letterbox SxS + CLIP normalize. S = model.InputSize
        // (z preprocessor_config.json przez ModelSeeder; base patch16 = 960, large patch14 = 1008).
        int inputSize = model.InputSize > 0 ? model.InputSize : DefaultInputSize;
        var (pixelValues, scale, padX, padY) = PreprocessImage(image, inputSize);

        // 2. Tokenize prompts (override max_length=16 dla OWLv2)
        var inputIds = new DenseTensor<long>([prompts.Count, OwlV2MaxTokens]);
        var attnMask = new DenseTensor<long>([prompts.Count, OwlV2MaxTokens]);
        for (int n = 0; n < prompts.Count; n++)
        {
            var tok = _tokenizer.TokenizeWithMask(prompts[n], OwlV2MaxTokens);
            for (int j = 0; j < OwlV2MaxTokens; j++)
            {
                inputIds[n, j] = tok.Ids[j];
                attnMask[n, j] = tok.AttentionMask[j];
            }
        }

        // 3. Run inference (3 inputs)
        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("pixel_values", pixelValues),
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attnMask),
        };
        using var outputs = session.Run(inputs);

        // 4. Find logits + pred_boxes outputs by name (model has 4 outputs total)
        Tensor<float>? logits = null, predBoxes = null;
        foreach (var o in outputs)
        {
            if (o.Name == "logits") logits = o.AsTensor<float>();
            else if (o.Name == "pred_boxes") predBoxes = o.AsTensor<float>();
        }
        if (logits is null || predBoxes is null)
            return DetectionResult.Failed("OWLv2: brak oczekiwanych outputów 'logits' / 'pred_boxes'.");

        // 5. Postprocess: sigmoid logits, denormalize boxes, NMS
        var detections = Postprocess(logits, predBoxes, prompts, model, scale, padX, padY, srcW, srcH, inputSize);

        sw.Stop();
        return new DetectionResult(true, detections, srcW, srcH, sw.Elapsed);
    }

    // ─── Preprocessing ─────────────────────────────────────────────────────

    private static (DenseTensor<float> Tensor, float Scale, int PadX, int PadY)
        PreprocessImage(Image<Rgb24> src, int inputSize)
    {
        var scale = Math.Min((float)inputSize / src.Width, (float)inputSize / src.Height);
        var newW = (int)Math.Round(src.Width * scale);
        var newH = (int)Math.Round(src.Height * scale);
        var padX = (inputSize - newW) / 2;
        var padY = (inputSize - newH) / 2;

        using var resized = src.Clone(ctx => ctx.Resize(newW, newH));
        using var canvas = new Image<Rgb24>(inputSize, inputSize,
            new Rgb24(LetterboxPadGray, LetterboxPadGray, LetterboxPadGray));
        canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(padX, padY), 1f));

        var tensor = new DenseTensor<float>([1, 3, inputSize, inputSize]);
        canvas.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < inputSize; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < inputSize; x++)
                {
                    var p = row[x];
                    // /255 then (v-mean)/std per channel, CLIP-style
                    tensor[0, 0, y, x] = (p.R / 255f - CLIP_MEAN[0]) / CLIP_STD[0];
                    tensor[0, 1, y, x] = (p.G / 255f - CLIP_MEAN[1]) / CLIP_STD[1];
                    tensor[0, 2, y, x] = (p.B / 255f - CLIP_MEAN[2]) / CLIP_STD[2];
                }
            }
        });

        return (tensor, scale, padX, padY);
    }

    // ─── Postprocessing ────────────────────────────────────────────────────

    private static List<Detection> Postprocess(
        Tensor<float> logits,         // [1, 3600, N] raw scores
        Tensor<float> predBoxes,      // [1, 3600, 4] cxywh normalized [0,1] in padded space
        IReadOnlyList<string> prompts,
        MLModel model,
        float scale, int padX, int padY, int srcW, int srcH,
        int inputSize)
    {
        var ld = logits.Dimensions.ToArray();
        var bd = predBoxes.Dimensions.ToArray();
        if (ld.Length != 3 || bd.Length != 3 || ld[0] != 1 || bd[0] != 1) return [];
        int n = Math.Min(ld[1], bd[1]);
        int nc = Math.Min(ld[2], prompts.Count);
        if (nc <= 0 || n <= 0) return [];

        float confTh = (float)model.ConfidenceThreshold;
        var raw = new List<(float x1, float y1, float x2, float y2, float conf, int cls)>(128);

        for (int i = 0; i < n; i++)
        {
            float bestScore = 0f;
            int bestCls = -1;
            // Sigmoid + argmax across classes
            for (int c = 0; c < nc; c++)
            {
                var logit = logits[0, i, c];
                var score = Sigmoid(logit);
                if (score > bestScore) { bestScore = score; bestCls = c; }
            }
            if (bestCls < 0 || bestScore < confTh) continue;

            // Decode box: cxywh normalized [0,1] in InputSize×InputSize padded space → xyxy in input-space
            var cx = predBoxes[0, i, 0] * inputSize;
            var cy = predBoxes[0, i, 1] * inputSize;
            var w = predBoxes[0, i, 2] * inputSize;
            var h = predBoxes[0, i, 3] * inputSize;
            var x1 = cx - w / 2;
            var y1 = cy - h / 2;
            var x2 = cx + w / 2;
            var y2 = cy + h / 2;
            raw.Add((x1, y1, x2, y2, bestScore, bestCls));
        }

        var kept = new List<Detection>();
        foreach (var group in raw.GroupBy(r => r.cls))
        {
            var sorted = group.OrderByDescending(r => r.conf).ToList();
            var survivors = new List<(float x1, float y1, float x2, float y2, float conf, int cls)>();
            foreach (var cand in sorted)
            {
                bool suppressed = false;
                foreach (var s in survivors)
                    if (IoUXyxy(cand, s) > model.IouThreshold) { suppressed = true; break; }
                if (!suppressed) survivors.Add(cand);
            }
            foreach (var s in survivors)
            {
                // Letterbox-undo: 960-space → original
                float x1 = (s.x1 - padX) / scale;
                float y1 = (s.y1 - padY) / scale;
                float x2 = (s.x2 - padX) / scale;
                float y2 = (s.y2 - padY) / scale;
                x1 = Math.Clamp(x1, 0, srcW); y1 = Math.Clamp(y1, 0, srcH);
                x2 = Math.Clamp(x2, 0, srcW); y2 = Math.Clamp(y2, 0, srcH);
                if (x2 <= x1 || y2 <= y1) continue;

                string label = s.cls < prompts.Count ? prompts[s.cls]
                    : (s.cls < model.Labels.Count ? model.Labels[s.cls] : $"class_{s.cls}");
                kept.Add(new Detection(s.cls, label, s.conf, new BoundingBox(x1, y1, x2 - x1, y2 - y1)));
            }
        }
        return kept;
    }

    private static float Sigmoid(float x) => 1f / (1f + (float)Math.Exp(-x));

    private static float IoUXyxy(
        (float x1, float y1, float x2, float y2, float conf, int cls) a,
        (float x1, float y1, float x2, float y2, float conf, int cls) b)
    {
        float ix = Math.Max(0, Math.Min(a.x2, b.x2) - Math.Max(a.x1, b.x1));
        float iy = Math.Max(0, Math.Min(a.y2, b.y2) - Math.Max(a.y1, b.y1));
        float inter = ix * iy;
        float aArea = Math.Max(0, a.x2 - a.x1) * Math.Max(0, a.y2 - a.y1);
        float bArea = Math.Max(0, b.x2 - b.x1) * Math.Max(0, b.y2 - b.y1);
        float union = aArea + bArea - inter;
        return union <= 0 ? 0 : inter / union;
    }

    // ─── Session management ────────────────────────────────────────────────

    private InferenceSession GetOrCreateSession(string modelId, string absPath)
    {
        lock (_sessionLock)
        {
            if (_sessions.TryGetValue(modelId, out var s)) return s;
            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            var session = new InferenceSession(absPath, opts);
            _sessions[modelId] = session;
            _log.LogInformation("OWLv2 session załadowany dla {Id} z {Path} (inputs: {In}, outputs: {Out})",
                modelId, absPath, session.InputMetadata.Count, session.OutputMetadata.Count);
            return session;
        }
    }

    private static string? ResolveOnnxPath(MLModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.OnnxAbsolutePath)) return model.OnnxAbsolutePath;
        return model.OnnxRelativePath;
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
