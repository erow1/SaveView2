using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SafeView.Application.Abstractions.ML;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.ML;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SafeView.ML.YoloWorld;

/// <summary>
/// <para>YOLO-World (open-vocabulary) detector — <b>Apache 2.0</b>. Implementuje
/// <see cref="IObjectDetector"/> + <see cref="ITextPromptDetector"/>.</para>
///
/// <para><b>Dwa tryby</b>, rozpoznawane automatycznie z liczby input-ów ONNX:</para>
/// <list type="bullet">
///   <item><b>Dynamic</b> (2 inputs: image + text_embeddings) — prompty obliczane w locie przez
///     <see cref="IClipTextEncoder"/>. Model nie ma vocab zapieczonego w wagach. Wolniejszy o
///     koszt CLIP encode, za to dowolny vocab runtime.</item>
///   <item><b>Compiled</b> (1 input: image) — tekst zamrożony w wagach przez re-parametryzację
///     (Faza 5). Działa jak closed-set YOLO. <see cref="MLModel.Labels"/> to zamrożony vocab.</item>
/// </list>
///
/// <para>Preprocessing obrazu: letterbox → 640×640 → RGB float [0,1] CHW.<br/>
/// Postprocessing: output <c>[B, 4+nc, N]</c> (YOLOv8-style), filter po confidence, NMS per-class,
/// rescale bbox do piksel oryginalnego obrazu. <c>nc</c> = liczba promptów (dynamic) albo
/// <c>model.Labels.Count</c> (compiled).</para>
///
/// <para>Text embedding cache: liczone per-call (dla pojedynczej klatki prompty się nie zmieniają
/// i zwykle są już gotowe po stronie pipeline). Jeśli wydajność staje się wąskim gardłem,
/// caller może cache'ować embeddings zewnętrznie (<c>DetectionClass.Id</c> → embedding).</para>
/// </summary>
public sealed class OnnxYoloWorldDetector : IObjectDetector, ITextPromptDetector, IBatchObjectDetector, IDisposable
{
    public string Backend => "YoloWorld";

    private readonly IClipTextEncoder _textEncoder;
    private readonly ICompiledPromptPackRepository? _packs;
    private readonly ILogger<OnnxYoloWorldDetector> _log;
    private readonly Lock _sessionLock = new();
    private readonly Dictionary<string, InferenceSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _dynamicFlags = new(StringComparer.Ordinal);

    public OnnxYoloWorldDetector(
        IClipTextEncoder textEncoder,
        ILogger<OnnxYoloWorldDetector> log,
        ICompiledPromptPackRepository? packs = null)
    {
        _textEncoder = textEncoder;
        _packs = packs; // optional — brak repo oznacza że cache packów jest wyłączony
        _log = log;
    }

    // ─── IObjectDetector ───────────────────────────────────────────────────

    public async Task<DetectionResult> DetectAsync(MLModel model, string imagePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        // Fallback: użyj model.Labels jako promptów (scenariusz closed-set / compiled).
        // Dla dynamic session to i tak działa — labels są promptami.
        if (model.Labels.Count == 0)
            return DetectionResult.Failed(
                "YOLO-World: model nie ma Labels ani nie dostarczono promptów. " +
                "Dodaj Labels w /models albo użyj DetectWithPromptsAsync.");
        return await DetectWithPromptsAsync(model, imagePath, model.Labels, ct).ConfigureAwait(false);
    }

    // ─── ITextPromptDetector ───────────────────────────────────────────────

    public bool SupportsDynamicPrompts(MLModel model)
    {
        try
        {
            var path = ResolveOnnxPath(model);
            if (path is null) return false;
            var session = GetOrCreateSession(model.Id, path);
            return _dynamicFlags.TryGetValue(model.Id, out var isDynamic) && isDynamic;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DetectionResult> DetectWithPromptsAsync(
        MLModel model, string imagePath, IReadOnlyList<string> prompts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(prompts);
        if (prompts.Count == 0)
            return DetectionResult.Failed("YOLO-World: lista promptów jest pusta.");

        var absPath = ResolveOnnxPath(model);
        if (absPath is null)
            return DetectionResult.Failed("YOLO-World: nie ustawiono OnnxAbsolutePath/RelativePath.");
        if (!File.Exists(absPath))
            return DetectionResult.Failed($"YOLO-World: plik modelu nie istnieje: {absPath}");

        InferenceSession session;
        bool isDynamic;
        try
        {
            session = GetOrCreateSession(model.Id, absPath);
            isDynamic = _dynamicFlags[model.Id];
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "YOLO-World: nie udało się wczytać sesji ONNX dla {Model}", model.Name);
            return DetectionResult.Failed($"Nie udało się wczytać modelu: {ex.Message}");
        }

        // ── Defensive guard (bug 2026-04-27): compiled-mode model + custom prompts ──
        // Compiled YOLO-World ma vocabulary zamrożone w wagach (1 input, brak text path).
        // Custom prompts są ignorowane przez sieć, ale dawniej silently relabel-owaliśmy
        // detekcje przez prompts[s.cls] — co produkowało "person z labelem dog". Teraz failujemy
        // jasno gdy caller przesłał prompty inne niż model.Labels (czyli explicite chce text-prompt).
        bool callerOverridesLabels =
            prompts.Count != model.Labels.Count
            || !prompts.SequenceEqual(model.Labels, StringComparer.Ordinal);
        if (!isDynamic && callerOverridesLabels)
        {
            return DetectionResult.Failed(
                "Model YOLO-World jest skompilowany z zamrożonym vocabulary (1 input ONNX, brak text path). " +
                "Custom text prompts NIE wpływają na inferencję. Pobierz dynamic export modelu " +
                "(scripts/download-models.sh yolo-world-v2-s).");
        }

        var sw = Stopwatch.StartNew();

        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct).ConfigureAwait(false);
        int srcW = image.Width, srcH = image.Height;

        var (imageTensor, scale, padX, padY) = Preprocess(image, model.InputSize);
        var inputs = new List<NamedOnnxValue>(2);
        var imageInputName = session.InputMetadata.Keys.First();
        inputs.Add(NamedOnnxValue.CreateFromTensor(imageInputName, imageTensor));

        if (isDynamic)
        {
            // Pack lookup — próbujemy pominąć CLIP encode gdy istnieje compiled pack
            // dla tego modelu + encodera + zestawu promptów. Cache hit = ~10-30ms oszczędności/klatkę.
            var (embeds, embedDim, orderedPrompts) = await ResolveEmbeddingsAsync(model, prompts, ct).ConfigureAwait(false);
            // Gdy pack był użyty, kolejność w orderedPrompts może się różnić od input prompts —
            // postprocess przypisze Label zgodnie z orderedPrompts (fallback do model.Labels).
            prompts = orderedPrompts;

            var textTensor = new DenseTensor<float>([1, prompts.Count, embedDim]);
            for (int i = 0; i < prompts.Count; i++)
                for (int j = 0; j < embedDim; j++)
                    textTensor[0, i, j] = embeds[i * embedDim + j];

            var textInputName = FindTextInputName(session);
            inputs.Add(NamedOnnxValue.CreateFromTensor(textInputName, textTensor));
        }

        using var outputs = session.Run(inputs);

        // NumClasses dla postprocess: liczba promptów (dynamic) albo model.Labels.Count (compiled).
        int nc = isDynamic ? prompts.Count : Math.Max(1, model.Labels.Count);

        // Output format autodetect:
        //  • 1 output  → YOLOv8 fused [1, 4+nc, N]      (compiled / ultralytics-export)
        //  • 2 outputs → split scores [1,N,nc] + boxes [1,N,4] xyxy in input-space
        //                (jquadrino/yolo-world-onnx i podobne dynamic exports)
        List<Detection> detections;
        if (outputs.Count >= 2)
        {
            var scoresOut = outputs[0].AsTensor<float>();
            var boxesOut = outputs[1].AsTensor<float>();
            // Niektóre exporty mogą mieć kolejność boxes-first; rozróżniamy po kształcie.
            if (scoresOut.Dimensions.Length == 3 && scoresOut.Dimensions[2] == 4
                && boxesOut.Dimensions.Length == 3 && boxesOut.Dimensions[2] == nc)
            {
                (scoresOut, boxesOut) = (boxesOut, scoresOut);
            }
            detections = PostprocessSplit(scoresOut, boxesOut, prompts, nc, model,
                scale, padX, padY, srcW, srcH);
        }
        else
        {
            var output = outputs[0].AsTensor<float>();
            detections = Postprocess(output, prompts, nc, model, scale, padX, padY, srcW, srcH);
        }

        sw.Stop();
        return new DetectionResult(true, detections, srcW, srcH, sw.Elapsed);
    }

    // ─── IBatchObjectDetector ──────────────────────────────────────────────

    /// <summary>
    /// Batched inference dla N obrazów z tymi samymi promptami (<see cref="MLModel.Labels"/>).
    /// Typowy scenariusz: SAHI tiles jednego ROI — wszystkie analizowane tym samym vocabulary.
    /// Gdy potrzebujesz innych promptów per-image, wołaj <see cref="DetectWithPromptsAsync"/>
    /// N razy (brak wsparcia dla heterogeneous batch w tej iteracji).
    ///
    /// <para>Optymalizacja: text embeddings liczymy raz (koszt CLIP encode ~10-30ms), potem
    /// replikujemy do batch size albo polegamy na modelach z batch-broadcast text input.</para>
    /// </summary>
    public bool SupportsBatching(MLModel model)
    {
        try
        {
            var abs = ResolveOnnxPath(model);
            if (abs is null) return false;
            var session = GetOrCreateSession(model.Id, abs);
            var imageInputDims = session.InputMetadata.Values.First().Dimensions;
            // Dynamic batch = dim[0] ≤ 0 (-1 reprezentuje symbol nazwany)
            return imageInputDims.Length > 0 && imageInputDims[0] <= 0;
        }
        catch { return false; }
    }

    public async Task<DetectionResult[]> DetectBatchAsync(
        MLModel model, IReadOnlyList<string> imagePaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count == 0) return [];

        // Fallback: model nie wspiera dynamic batch → iteracja sekwencyjna (preserving correctness).
        if (!SupportsBatching(model))
        {
            var seq = new DetectionResult[imagePaths.Count];
            for (int i = 0; i < imagePaths.Count; i++)
                seq[i] = await DetectAsync(model, imagePaths[i], ct).ConfigureAwait(false);
            return seq;
        }

        if (model.Labels.Count == 0)
        {
            var err = DetectionResult.Failed("YOLO-World batch: model nie ma Labels — ustaw prompty w /models.");
            return Enumerable.Repeat(err, imagePaths.Count).ToArray();
        }

        var absPath = ResolveOnnxPath(model);
        if (absPath is null)
            return Enumerable.Repeat(DetectionResult.Failed("OnnxPath nieustawiony"), imagePaths.Count).ToArray();

        var session = GetOrCreateSession(model.Id, absPath);
        var isDynamic = _dynamicFlags[model.Id];
        var sw = Stopwatch.StartNew();

        int n = imagePaths.Count;
        int size = model.InputSize;
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

        var inputs = new List<NamedOnnxValue>(2);
        var imageInputName = session.InputMetadata.Keys.First();
        inputs.Add(NamedOnnxValue.CreateFromTensor(imageInputName, batchTensor));

        var prompts = model.Labels;
        if (isDynamic)
        {
            // Text embeddings liczymy raz (koszt CLIP) i replikujemy N razy w batch dim.
            var flatEmbeds = await _textEncoder.EncodeAsync(prompts, ct).ConfigureAwait(false);
            var dim = _textEncoder.EmbeddingDimension;
            var textTensor = new DenseTensor<float>([n, prompts.Count, dim]);
            for (int b = 0; b < n; b++)
                for (int i = 0; i < prompts.Count; i++)
                    for (int j = 0; j < dim; j++)
                        textTensor[b, i, j] = flatEmbeds[i * dim + j];

            var textInputName = FindTextInputName(session);
            inputs.Add(NamedOnnxValue.CreateFromTensor(textInputName, textTensor));
        }

        using var outputs = session.Run(inputs);
        sw.Stop();

        int nc = isDynamic ? prompts.Count : Math.Max(1, model.Labels.Count);
        var results = new DetectionResult[n];

        if (outputs.Count >= 2)
        {
            // Split format (jquadrino i podobne dynamic) — scores [n,N,nc] + boxes [n,N,4] xyxy
            var scoresOut = outputs[0].AsTensor<float>();
            var boxesOut = outputs[1].AsTensor<float>();
            if (scoresOut.Dimensions.Length == 3 && scoresOut.Dimensions[2] == 4
                && boxesOut.Dimensions.Length == 3 && boxesOut.Dimensions[2] == nc)
            {
                (scoresOut, boxesOut) = (boxesOut, scoresOut);
            }
            for (int k = 0; k < n; k++)
            {
                var dets = PostprocessSplitBatchItem(scoresOut, boxesOut, k, prompts.ToList(), nc, model,
                    scales[k], padXs[k], padYs[k], srcSizes[k].W, srcSizes[k].H);
                results[k] = new DetectionResult(true, dets, srcSizes[k].W, srcSizes[k].H,
                    TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds / (double)n));
            }
        }
        else
        {
            // Fused YOLOv8 format (compiled / ultralytics export)
            var output = outputs[0].AsTensor<float>();
            for (int k = 0; k < n; k++)
            {
                var dets = PostprocessBatchItem(output, k, prompts.ToList(), nc, model,
                    scales[k], padXs[k], padYs[k], srcSizes[k].W, srcSizes[k].H);
                results[k] = new DetectionResult(true, dets, srcSizes[k].W, srcSizes[k].H,
                    TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds / (double)n));
            }
        }

        _log.LogDebug("YOLO-World batch: {N} obrazów w {Ms}ms ({Per}ms/image)",
            n, sw.ElapsedMilliseconds, sw.ElapsedMilliseconds / (double)n);

        return results;
    }

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

    private static List<Detection> PostprocessSplitBatchItem(
        Tensor<float> scoresOut, Tensor<float> boxesOut, int batchIdx,
        List<string> prompts, int nc,
        MLModel model, float scale, int padX, int padY, int srcW, int srcH)
    {
        var sd = scoresOut.Dimensions.ToArray();
        var bd = boxesOut.Dimensions.ToArray();
        if (sd.Length != 3 || bd.Length != 3) return [];
        if (batchIdx >= sd[0] || batchIdx >= bd[0]) return [];

        int n = Math.Min(sd[1], bd[1]);
        int classCount = Math.Min(sd[2], nc);
        if (classCount <= 0 || n <= 0) return [];

        float confTh = (float)model.ConfidenceThreshold;
        var raw = new List<(float x1, float y1, float x2, float y2, float conf, int cls)>(128);
        for (int i = 0; i < n; i++)
        {
            float bestConf = 0f;
            int bestCls = -1;
            for (int c = 0; c < classCount; c++)
            {
                var s = scoresOut[batchIdx, i, c];
                if (s > bestConf) { bestConf = s; bestCls = c; }
            }
            if (bestCls < 0 || bestConf < confTh) continue;
            raw.Add((boxesOut[batchIdx, i, 0], boxesOut[batchIdx, i, 1],
                     boxesOut[batchIdx, i, 2], boxesOut[batchIdx, i, 3],
                     bestConf, bestCls));
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

    private static List<Detection> PostprocessBatchItem(
        Tensor<float> output, int batchIdx,
        List<string> prompts, int nc,
        MLModel model, float scale, int padX, int padY, int srcW, int srcH)
    {
        var dims = output.Dimensions.ToArray();
        if (dims.Length != 3 || batchIdx >= dims[0]) return [];

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
                cx = output[batchIdx, 0, i];
                cy = output[batchIdx, 1, i];
                w  = output[batchIdx, 2, i];
                h  = output[batchIdx, 3, i];
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
                w  = output[batchIdx, i, 2];
                h  = output[batchIdx, i, 3];
                for (int c = 0; c < nc; c++)
                {
                    var s = output[batchIdx, i, 4 + c];
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

                string label;
                if (s.cls < prompts.Count) label = prompts[s.cls];
                else if (s.cls < model.Labels.Count) label = model.Labels[s.cls];
                else label = $"class_{s.cls}";
                kept.Add(new Detection(s.cls, label, s.conf, new BoundingBox(x1, y1, x2 - x1, y2 - y1)));
            }
        }
        return kept;
    }

    // ─── Embeddings resolution: cache-first, encoder-fallback ──────────────

    /// <summary>
    /// Rozwiązuje text embeddings dla podanych promptów — najpierw sprawdza <see cref="ICompiledPromptPackRepository"/>
    /// (gdy dostępny) pod kątem kompatybilnego packa, potem fallback do <see cref="IClipTextEncoder"/>.
    /// Zwraca flat array (N × dim), dim, oraz prompty w kolejności jaką mają embeddings —
    /// przy cache hit kolejność pochodzi z packa (może różnić się od podanej na wejściu).
    /// </summary>
    private async Task<(float[] Embeddings, int Dim, IReadOnlyList<string> OrderedPrompts)>
        ResolveEmbeddingsAsync(MLModel model, IReadOnlyList<string> prompts, CancellationToken ct)
    {
        if (_packs is not null)
        {
            try
            {
                var pack = await _packs.FindByPromptsAsync(model.Id, _textEncoder.Backend, prompts, ct)
                    .ConfigureAwait(false);
                if (pack is not null && pack.EmbeddingsBlob.Length > 0)
                {
                    // Byte blob → float[]
                    int floatCount = pack.EmbeddingsBlob.Length / sizeof(float);
                    var flat = new float[floatCount];
                    Buffer.BlockCopy(pack.EmbeddingsBlob, 0, flat, 0, pack.EmbeddingsBlob.Length);
                    _log.LogDebug("YOLO-World pack cache hit: '{Name}' ({N} prompts, skip CLIP encode)",
                        pack.Name, pack.Prompts.Count);
                    return (flat, pack.EmbeddingDimension, pack.Prompts);
                }
            }
            catch (Exception ex)
            {
                // Pack lookup failure nie blokuje inferencji — logujemy i liczymy normalnie.
                _log.LogDebug(ex, "YOLO-World pack lookup failed — fallback do CLIP encode");
            }
        }

        var encoded = await _textEncoder.EncodeAsync(prompts, ct).ConfigureAwait(false);
        return (encoded, _textEncoder.EmbeddingDimension, prompts);
    }

    // ─── Session management ────────────────────────────────────────────────

    private InferenceSession GetOrCreateSession(string modelId, string absPath)
    {
        lock (_sessionLock)
        {
            if (_sessions.TryGetValue(modelId, out var s)) return s;

            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            var session = new InferenceSession(absPath, opts);

            // Wykryj tryb: dynamic = 2 inputs (image + text), compiled = 1 input (image only).
            bool isDynamic = session.InputMetadata.Count >= 2;
            _sessions[modelId] = session;
            _dynamicFlags[modelId] = isDynamic;

            _log.LogInformation("YOLO-World session załadowany dla {Id} z {Path} (tryb: {Mode}, inputs: {N})",
                modelId, absPath, isDynamic ? "dynamic" : "compiled", session.InputMetadata.Count);
            return session;
        }
    }

    private static string FindTextInputName(InferenceSession session)
    {
        // Nazewnictwo różni się między exportami — szukamy po patternach.
        foreach (var name in session.InputMetadata.Keys)
        {
            if (name.Contains("text", StringComparison.OrdinalIgnoreCase)) return name;
            if (name.Contains("txt", StringComparison.OrdinalIgnoreCase)) return name;
            if (name.Contains("feat", StringComparison.OrdinalIgnoreCase)) return name;
            if (name.Contains("embed", StringComparison.OrdinalIgnoreCase)) return name;
            if (name.Contains("class", StringComparison.OrdinalIgnoreCase)) return name;
        }
        // Fallback: drugi input (pierwszy to zwykle image)
        return session.InputMetadata.Keys.Skip(1).First();
    }

    private static string? ResolveOnnxPath(MLModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.OnnxAbsolutePath))
            return model.OnnxAbsolutePath;
        return model.OnnxRelativePath; // gdy relative — caller musi zrobić ResolveAbsolutePath
    }

    // ─── Pre / Post ────────────────────────────────────────────────────────

    private static (DenseTensor<float> Tensor, float Scale, int PadX, int PadY)
        Preprocess(Image<Rgb24> src, int size)
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

    /// <summary>
    /// Postprocess dla split-output dynamic exportów (np. <c>jquadrino/yolo-world-onnx</c>):
    /// scores <c>[1, N, nc]</c> = sigmoid probs per (anchor, class), boxes <c>[1, N, 4]</c> =
    /// xyxy w 640×640 input-space (już zdekodowane, sometimes przed-NMS).
    /// </summary>
    private static List<Detection> PostprocessSplit(
        Tensor<float> scoresOut,
        Tensor<float> boxesOut,
        IReadOnlyList<string> prompts,
        int nc,
        MLModel model,
        float scale, int padX, int padY, int srcW, int srcH)
    {
        var sd = scoresOut.Dimensions.ToArray();
        var bd = boxesOut.Dimensions.ToArray();
        if (sd.Length != 3 || bd.Length != 3 || sd[0] != 1 || bd[0] != 1) return [];
        int n = Math.Min(sd[1], bd[1]);
        int classCount = Math.Min(sd[2], nc);
        if (classCount <= 0 || n <= 0) return [];

        float confTh = (float)model.ConfidenceThreshold;
        var raw = new List<(float x1, float y1, float x2, float y2, float conf, int cls)>(128);

        for (int i = 0; i < n; i++)
        {
            float bestConf = 0f;
            int bestCls = -1;
            for (int c = 0; c < classCount; c++)
            {
                var s = scoresOut[0, i, c];
                if (s > bestConf) { bestConf = s; bestCls = c; }
            }
            if (bestCls < 0 || bestConf < confTh) continue;
            raw.Add((boxesOut[0, i, 0], boxesOut[0, i, 1], boxesOut[0, i, 2], boxesOut[0, i, 3],
                     bestConf, bestCls));
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
                // Rescale: input-space xyxy → original image pixel xyxy
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

    private static List<Detection> Postprocess(
        Tensor<float> output,
        IReadOnlyList<string> prompts,
        int nc,
        MLModel model,
        float scale, int padX, int padY, int srcW, int srcH)
    {
        var dims = output.Dimensions.ToArray();
        if (dims.Length != 3 || dims[0] != 1) return [];

        // YOLOv8-style layout: [1, 4+nc, N] albo [1, N, 4+nc]. Autodetect.
        bool v8Layout = dims[1] == 4 + nc || (dims[1] < dims[2] && dims[1] >= 4);
        int numBoxes = v8Layout ? dims[2] : dims[1];

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

                // Label = klasa z vocabulary kondycjonującego sieć (dynamic: user prompts;
                // compiled: model.Labels — defensive guard powyżej blokuje custom prompts dla compiled).
                string label;
                if (s.cls < prompts.Count) label = prompts[s.cls];
                else if (s.cls < model.Labels.Count) label = model.Labels[s.cls];
                else label = $"class_{s.cls}";

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
            _dynamicFlags.Clear();
        }
    }
}
