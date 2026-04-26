namespace SafeView.Application.Abstractions.ML;

/// <summary>
/// Koduje listę promptów tekstowych w embeddings wektorowe zgodne z przestrzenią
/// visual-features modelu YOLO-World / YOLOE. Embedding jest wysyłany jako drugi
/// input do ONNX detekcji (obok obrazu) i model dopasowuje detekcje do najbliższych
/// klas po cosine similarity.
///
/// <para>Dwie implementacje (hot-swap przez <c>YoloWorldOptions.EncoderStrategy</c>):</para>
/// <list type="bullet">
///   <item><b>In-process ONNX</b> (<c>OnnxClipTextEncoder</c>) — domyślnie. CLIP ViT-B/32 text encoder
///     bundled w <c>runtime/models/yolo-world-v2-s/text-encoder.onnx</c>. Zero zależności
///     od sieci, offline-first OK.</item>
///   <item><b>External LLM</b> (<c>ExternalLlmClipTextEncoder</c>) — używa
///     <c>IChatClientFactory</c> + provider obsługujący embeddings endpoint (OpenAI, Azure).
///     Lżejsza binarka (bez CLIP ONNX ~150MB), ale wymaga zewnętrznej usługi.</item>
/// </list>
/// </summary>
public interface IClipTextEncoder
{
    /// <summary>Wymiar embeddingu — zależny od modelu. CLIP ViT-B/32 zwraca 512,
    /// ViT-L/14 zwraca 768. YOLO-World v2-s oczekuje 512.</summary>
    int EmbeddingDimension { get; }

    /// <summary>Nazwa backendu — dla logów / diagnostyki. Np. "onnx-clip-vit-b32" albo "openai-text-embedding-3-small".</summary>
    string Backend { get; }

    /// <summary>
    /// Liczy embeddings dla podanych promptów. Zwraca macierz <c>[prompts.Count, EmbeddingDimension]</c>
    /// w flat float array (row-major) — zgodnie z ONNX tensor layout.
    /// Rzuca gdy encoder nie jest dostępny (np. plik ONNX brakuje, provider unreachable).
    /// </summary>
    Task<float[]> EncodeAsync(IReadOnlyList<string> prompts, CancellationToken ct = default);
}
