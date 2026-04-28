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

    /// <summary>
    /// Pobiera model z registry dostawcy (instaluje go lokalnie). Streamuje progres
    /// przez <paramref name="progress"/>. Wspierane tylko dla Ollama (POST /api/pull,
    /// NDJSON stream); inne backendy zwracają false bez efektu.
    /// </summary>
    Task<bool> PullModelAsync(
        string modelName,
        IProgress<PullProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Status serwera LLM — wersja + lista modeli załadowanych do pamięci (z VRAM info).
    /// Wspierane tylko dla Ollama (GET /api/version + GET /api/ps); inne backendy zwracają
    /// <see cref="ServerStatus.Supported"/>=false.
    /// </summary>
    Task<ServerStatus> GetServerStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Usuwa model z dysku (na Ollama: DELETE /api/delete). Inne backendy → false.
    /// </summary>
    Task<bool> DeleteModelAsync(string modelName, CancellationToken ct = default);
}

/// <summary>
/// Postęp pobierania modelu — strumieniowane przez <see cref="IChatClient.PullModelAsync"/>.
/// Dla Ollama statusy obejmują: "pulling manifest", "downloading", "verifying sha256 digest",
/// "writing manifest", "success". Pola <c>CompletedBytes</c>/<c>TotalBytes</c> są wypełnione
/// tylko podczas downloadu pojedynczego layeru.
/// </summary>
public sealed record PullProgress(
    string? Status,
    long? CompletedBytes,
    long? TotalBytes);

/// <summary>
/// Pojedynczy model załadowany do pamięci serwera LLM.
/// </summary>
/// <param name="Name">Nazwa modelu (np. "qwen2.5vl:7b").</param>
/// <param name="SizeBytes">Łączny rozmiar modelu w pamięci (RAM+VRAM).</param>
/// <param name="VramBytes">Bajty w VRAM. <c>SizeBytes - VramBytes</c> = bajty w RAM (CPU).</param>
/// <param name="ContextLength">Aktywne okno kontekstu (tokeny).</param>
/// <param name="ParameterSize">Rozmiar parametrów (np. "7.6B").</param>
/// <param name="Quantization">Poziom kwantyzacji (np. "Q4_K_M").</param>
/// <param name="ExpiresAtUtc">Kiedy keep_alive wygaśnie i model zostanie wyładowany. Null = brak limitu.</param>
public sealed record LoadedModelInfo(
    string Name,
    long SizeBytes,
    long VramBytes,
    int ContextLength,
    string? ParameterSize,
    string? Quantization,
    DateTime? ExpiresAtUtc);

/// <summary>
/// Status serwera LLM. <see cref="Supported"/>=false dla backendów które nie eksponują
/// /api/ps + /api/version (czyli wszystko poza Ollama). Caller powinien wtedy ukryć panel.
/// </summary>
/// <param name="Supported">Czy backend wspiera tę informację.</param>
/// <param name="Version">Wersja serwera (np. "0.21.2" dla Ollama).</param>
/// <param name="LoadedModels">Modele aktualnie załadowane do pamięci.</param>
public sealed record ServerStatus(
    bool Supported,
    string? Version,
    IReadOnlyList<LoadedModelInfo> LoadedModels)
{
    public static ServerStatus NotSupported(string? reason = null)
        => new(false, reason, Array.Empty<LoadedModelInfo>());

    /// <summary>Suma <c>SizeBytes</c> wszystkich załadowanych modeli — łączne zużycie pamięci.</summary>
    public long TotalSizeBytes => LoadedModels.Sum(m => m.SizeBytes);

    /// <summary>Suma <c>VramBytes</c> — łączne zużycie VRAM przez aktywne modele.</summary>
    public long TotalVramBytes => LoadedModels.Sum(m => m.VramBytes);
}
