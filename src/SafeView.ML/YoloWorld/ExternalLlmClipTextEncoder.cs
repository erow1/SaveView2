using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.LLM;
using SafeView.Application.Abstractions.ML;

namespace SafeView.ML.YoloWorld;

/// <summary>
/// Alternatywna implementacja <see cref="IClipTextEncoder"/> używająca zewnętrznego endpointu
/// embeddings przez <see cref="IEmbeddingsClientFactory"/>. Wybierana gdy
/// <see cref="YoloWorldOptions.EncoderStrategy"/>=<see cref="ClipEncoderStrategy.ExternalLlm"/>.
///
/// <para><b>Ograniczenie semantyczne</b>: YOLO-World oczekuje embeddings z przestrzeni
/// CLIP ViT-B/32 (512-dim). Standardowe OpenAI <c>text-embedding-3-*</c> mają INNĄ przestrzeń —
/// nie zadziałają bez dodatkowego mapowania. Provider musi serwować CLIP-kompatybilne
/// embeddings (np. self-hosted CLIP przez LocalAI/vLLM).</para>
///
/// <para>Po uzyskaniu embeddings robimy L2-normalize (tak samo jak in-process encoder)
/// żeby zachować spójne cosine similarity po stronie detektora. Jeśli provider zwrócił
/// wektory o innej długości niż <see cref="EmbeddingDimension"/> — rzucamy jasny błąd.</para>
/// </summary>
public sealed class ExternalLlmClipTextEncoder : IClipTextEncoder
{
    public string Backend => "external-llm-embeddings";
    public int EmbeddingDimension => 512; // target space YOLO-World v2-s

    private readonly IEmbeddingsClientFactory _factory;
    private readonly IOptionsMonitor<YoloWorldOptions> _opts;
    private readonly ILogger<ExternalLlmClipTextEncoder> _log;

    public ExternalLlmClipTextEncoder(
        IEmbeddingsClientFactory factory,
        IOptionsMonitor<YoloWorldOptions> opts,
        ILogger<ExternalLlmClipTextEncoder> log)
    {
        _factory = factory;
        _opts = opts;
        _log = log;
    }

    public async Task<float[]> EncodeAsync(IReadOnlyList<string> prompts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        if (prompts.Count == 0) return [];

        var cfg = _opts.CurrentValue;
        var client = await _factory.GetForAsync(cfg.ExternalLlmProviderId, ct).ConfigureAwait(false);

        var embOptions = new EmbeddingsOptions(
            Model: cfg.ExternalLlmEmbeddingModel,
            Dimensions: EmbeddingDimension);

        var resp = await client.GetEmbeddingsAsync(prompts, embOptions, ct).ConfigureAwait(false);
        if (!resp.Success)
            throw new InvalidOperationException(
                $"External embeddings request failed (providerId='{cfg.ExternalLlmProviderId}', model='{cfg.ExternalLlmEmbeddingModel}'): {resp.ErrorMessage}");

        if (resp.Embeddings.Count != prompts.Count)
            throw new InvalidOperationException(
                $"Provider zwrócił {resp.Embeddings.Count} embeddings dla {prompts.Count} promptów — mismatch.");

        var result = new float[prompts.Count * EmbeddingDimension];
        for (int i = 0; i < prompts.Count; i++)
        {
            var vec = resp.Embeddings[i];
            if (vec.Length != EmbeddingDimension)
                throw new InvalidOperationException(
                    $"Provider zwrócił embedding o długości {vec.Length}, oczekiwano {EmbeddingDimension}. " +
                    "Upewnij się że używasz providera CLIP-kompatybilnego i model zwraca wektor 512-dim " +
                    "(OpenAI 3.x wymaga parametru 'dimensions').");

            // L2-normalize
            double sumSq = 0;
            for (int j = 0; j < vec.Length; j++) sumSq += vec[j] * vec[j];
            var norm = (float)Math.Sqrt(sumSq);
            var inv = norm > 1e-6f ? 1f / norm : 1f;

            for (int j = 0; j < vec.Length; j++)
                result[i * EmbeddingDimension + j] = vec[j] * inv;
        }

        _log.LogDebug("External embeddings: {N} promptów → {D}-dim wektory w {Ms}ms",
            prompts.Count, EmbeddingDimension, resp.Latency.TotalMilliseconds);

        return result;
    }
}
