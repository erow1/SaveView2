using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SafeView.Application.Abstractions.ML;

namespace SafeView.ML.YoloWorld;

/// <summary>
/// CLIP text encoder (ViT-B/32) — in-process, ONNX Runtime. Domyślna implementacja
/// <see cref="IClipTextEncoder"/> dla offline-first scenariusza.
///
/// <para>Model ONNX + tokenizer files są oczekiwane w:</para>
/// <list type="bullet">
///   <item><c>runtime/models/yolo-world-v2-s/text-encoder.onnx</c> — CLIP text ONNX</item>
///   <item><c>runtime/models/yolo-world-v2-s/tokenizer/vocab.json</c></item>
///   <item><c>runtime/models/yolo-world-v2-s/tokenizer/merges.txt</c></item>
/// </list>
///
/// <para>Session + tokenizer cache'owane singleton (thread-safe, lock na session).
/// Model i tokenizer są lazy-inicjalizowane przy pierwszym <see cref="EncodeAsync"/>
/// — nie blokuje startu aplikacji jeśli pliki brakują.</para>
/// </summary>
public sealed class OnnxClipTextEncoder : IClipTextEncoder, IDisposable
{
    public string Backend => "onnx-clip-vit-b32";
    public int EmbeddingDimension => 512; // CLIP ViT-B/32 — YOLO-World v2-s oczekuje 512

    private readonly ClipTokenizer _tokenizer;
    private readonly string _modelPath;
    private readonly ILogger<OnnxClipTextEncoder> _log;
    private readonly Lock _sessionLock = new();
    private InferenceSession? _session;

    public OnnxClipTextEncoder(string modelPath, ClipTokenizer tokenizer, ILogger<OnnxClipTextEncoder> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(tokenizer);
        _modelPath = modelPath;
        _tokenizer = tokenizer;
        _log = log;
    }

    public async Task<float[]> EncodeAsync(IReadOnlyList<string> prompts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        if (prompts.Count == 0) return [];

        var session = GetOrCreateSession();

        // Tokenize wszystkie prompty → [N, contextLength] tensor
        var tokenBatches = new long[prompts.Count][];
        int contextLen = 0;
        for (int i = 0; i < prompts.Count; i++)
        {
            tokenBatches[i] = _tokenizer.Tokenize(prompts[i]);
            if (i == 0) contextLen = tokenBatches[i].Length;
        }

        var inputTensor = new DenseTensor<long>([prompts.Count, contextLen]);
        for (int i = 0; i < prompts.Count; i++)
            for (int j = 0; j < contextLen; j++)
                inputTensor[i, j] = tokenBatches[i][j];

        var inputName = session.InputMetadata.Keys.First();
        using var outputs = await Task.Run(() =>
            session.Run([NamedOnnxValue.CreateFromTensor(inputName, inputTensor)]), ct).ConfigureAwait(false);

        // Output: [N, EmbeddingDimension]. Niektóre exporty zwracają już znormalizowane embeddings,
        // inne nie — robimy L2-normalize żeby mieć spójne cosine similarity po stronie detektora.
        var outputTensor = outputs[0].AsTensor<float>();
        var dims = outputTensor.Dimensions;
        if (dims.Length != 2 || dims[0] != prompts.Count)
            throw new InvalidOperationException(
                $"CLIP text encoder zwrócił nieoczekiwany kształt [{string.Join(",", dims.ToArray())}]");

        int dim = dims[1];
        var result = new float[prompts.Count * dim];
        for (int i = 0; i < prompts.Count; i++)
        {
            // L2-norm per-prompt
            double sumSq = 0;
            for (int j = 0; j < dim; j++)
            {
                var v = outputTensor[i, j];
                result[i * dim + j] = v;
                sumSq += v * v;
            }
            var norm = (float)Math.Sqrt(sumSq);
            if (norm > 1e-6f)
            {
                var inv = 1f / norm;
                for (int j = 0; j < dim; j++)
                    result[i * dim + j] *= inv;
            }
        }

        return result;
    }

    private InferenceSession GetOrCreateSession()
    {
        lock (_sessionLock)
        {
            if (_session is not null) return _session;

            if (!File.Exists(_modelPath))
                throw new FileNotFoundException(
                    $"CLIP text encoder ONNX nie istnieje: {_modelPath}. Uruchom scripts/download-models.sh yolo-world-v2-s.",
                    _modelPath);

            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _session = new InferenceSession(_modelPath, opts);
            _log.LogInformation("CLIP text encoder ONNX załadowany z {Path}", _modelPath);
            return _session;
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
