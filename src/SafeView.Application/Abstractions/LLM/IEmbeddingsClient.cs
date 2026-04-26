namespace SafeView.Application.Abstractions.LLM;

/// <summary>
/// Klient embeddings — wektoryzuje teksty przez OpenAI-compatible endpoint
/// (<c>POST /v1/embeddings</c>). Obsługiwany przez OpenAI, Azure OpenAI, LocalAI,
/// niektóre vLLM deploymenty.
///
/// <para><b>Uwaga o zgodności przestrzeni latent</b>: YOLO-World oczekuje embeddings
/// z przestrzeni CLIP ViT-B/32 (512-dim). Standardowe modele OpenAI (text-embedding-3-*)
/// mają INNĄ przestrzeń i nie zadziałają z YOLO-World bez dodatkowego mapowania.
/// External encoder dla YOLO-World ma sens tylko gdy provider serwuje CLIP-kompatybilne
/// embeddings (np. self-hosted CLIP as-a-service przez LocalAI/vLLM z załadowanym CLIP-em).</para>
/// </summary>
public interface IEmbeddingsClient
{
    string Backend { get; }

    /// <summary>Liczy embeddings dla listy tekstów. Model i dimensions mogą być wymuszone
    /// (niektóre providery wymagają dokładnego dim w przypadku docelowej przestrzeni).</summary>
    Task<EmbeddingsResponse> GetEmbeddingsAsync(
        IReadOnlyList<string> inputs,
        EmbeddingsOptions? options = null,
        CancellationToken ct = default);
}

public sealed record EmbeddingsOptions(
    string? Model = null,
    /// <summary>Wymuszony wymiar embedding (OpenAI 3.x: <c>dimensions</c> param). Null = default modelu.</summary>
    int? Dimensions = null);

public sealed record EmbeddingsResponse(
    bool Success,
    /// <summary>Jedna tablica float per input (w kolejności); każda o długości <c>Dimensions</c>.</summary>
    IReadOnlyList<float[]> Embeddings,
    string? Model,
    int TotalTokens,
    TimeSpan Latency,
    string? ErrorMessage = null)
{
    public static EmbeddingsResponse Failed(string error) =>
        new(false, [], null, 0, TimeSpan.Zero, error);
}
