namespace SafeView.Application.Abstractions.LLM;

public enum ChatRole { System, User, Assistant }

/// <summary>
/// Wiadomość chat — tekst (zawsze) + opcjonalny obraz (multimodal/VLLM).
/// </summary>
/// <param name="Role">System / User / Assistant.</param>
/// <param name="Content">Tekst wiadomości.</param>
/// <param name="ImagePath">Absolutna ścieżka do pliku JPG/PNG — klient wczyta i zakoduje jako base64. Null = text-only.</param>
public sealed record ChatMessage(ChatRole Role, string Content, string? ImagePath = null);

public sealed record ChatOptions(
    string? Model = null,
    double Temperature = 0.2,
    int MaxTokens = 1024,
    /// <summary>
    /// Opcjonalny JSON Schema (jako string) — wymusza format odpowiedzi.
    /// vLLM: przekazane jako extra_body.guided_json. OpenAI: response_format json_schema.
    /// Backendy bez wsparcia (Ollama bez tagów grammatical) ignorują i polegają na prompcie.
    /// </summary>
    string? JsonSchema = null);

public sealed record ChatResponse(
    bool Success,
    string Content,
    string? Model,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Latency,
    string? ErrorMessage = null);

/// <summary>
/// Uniwersalna abstrakcja klienta LLM (OpenAI-compatible: vLLM, Ollama, LM Studio, OpenAI).
/// Wspiera multimodal gdy <see cref="ChatMessage.ImagePath"/> jest ustawione.
/// </summary>
public interface IChatClient
{
    string Backend { get; }

    Task<ChatResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Sprawdza dostępność (ping na /models lub /health).</summary>
    Task<bool> PingAsync(CancellationToken ct = default);

    /// <summary>
    /// Pobiera listę modeli dostępnych u dostawcy (<c>GET /v1/models</c>, standard OpenAI).
    /// Wszystkie mainstream-owe OpenAI-compat backendy obsługują (Ollama, vLLM, LM Studio,
    /// OpenAI, Groq, Together, Mistral). Zwraca pustą listę gdy endpoint nie istnieje
    /// albo provider jest niedostępny — caller decyduje jak to pokazać użytkownikowi.
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Unload modelu z pamięci (force fresh load on next request). Używane gdy model
    /// "się zafiksował" (deterministyczne nonsense responses powtarzające się mimo zmiany
    /// promptu — typowy objaw zalegających KV-cache albo zepsutej sesji w niektórych
    /// backendach).
    ///
    /// Implementacje:
    ///  • backend "ollama" — POST /api/generate z <c>keep_alive: 0</c> → Ollama natychmiast
    ///    zwalnia model z RAM/VRAM. Następne wywołanie ChatAsync triggeruje cold load.
    ///  • inne backendy — no-op zwracające false. vLLM/OpenAI/Groq są bezstanowe per-request,
    ///    "restart" nie ma sensu po stronie klienta.
    ///
    /// Zwraca true gdy backend wspiera unload i operacja się udała. False = nieobsługiwane
    /// lub błąd (sprawdź logi).
    /// </summary>
    Task<bool> UnloadModelAsync(string? modelName = null, CancellationToken ct = default);
}
