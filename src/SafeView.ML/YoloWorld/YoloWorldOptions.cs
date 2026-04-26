namespace SafeView.ML.YoloWorld;

/// <summary>Strategia liczenia text embeddings dla YOLO-World inferencji.</summary>
public enum ClipEncoderStrategy
{
    /// <summary>Default — CLIP text encoder załadowany jako osobny ONNX obok modelu detekcji
    /// w <c>runtime/models/yolo-world-v2-s/text-encoder.onnx</c>. Offline-first OK.</summary>
    InProcessOnnx = 0,

    /// <summary>Alternatywa — embeddings liczone przez zewnętrzne API LLM (OpenAI embeddings,
    /// Azure OpenAI, itp.) wskazane przez <see cref="YoloWorldOptions.ExternalLlmProviderId"/>.
    /// Lżejsza binarka (bez CLIP ONNX ~150MB), ale wymaga providera z embeddings endpoint.</summary>
    ExternalLlm = 1
}

/// <summary>
/// Konfiguracja runtime dla YOLO-World detektora. Mapowana z <c>appsettings.json</c> sekcji
/// <c>YoloWorld</c>. Domyślne wartości są bezpieczne dla offline-first scenariusza.
/// </summary>
public sealed class YoloWorldOptions
{
    public const string SectionName = "YoloWorld";

    /// <summary>Strategia text encodera. Default <see cref="ClipEncoderStrategy.InProcessOnnx"/>.</summary>
    public ClipEncoderStrategy EncoderStrategy { get; set; } = ClipEncoderStrategy.InProcessOnnx;

    /// <summary>Gdy <see cref="EncoderStrategy"/>=<see cref="ClipEncoderStrategy.ExternalLlm"/> —
    /// ID providera z kolekcji <c>llm_providers</c>. Provider musi obsługiwać embeddings
    /// endpoint kompatybilny z OpenAI (<c>POST /v1/embeddings</c>).</summary>
    public string? ExternalLlmProviderId { get; set; }

    /// <summary>Nazwa modelu embeddings przy strategy=ExternalLlm (np. "text-embedding-3-small").
    /// Musi zwracać 512-dim wektor zgodny z YOLO-World v2-s text space.</summary>
    public string? ExternalLlmEmbeddingModel { get; set; }

    /// <summary>CLIP context length — standardowo 77 tokenów dla ViT-B/32 (używane w YOLO-World v2).</summary>
    public int TextContextLength { get; set; } = 77;
}
