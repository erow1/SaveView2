using SafeView.Domain.Common;

namespace SafeView.Domain.Llm;

/// <summary>
/// Typ dostawcy LLM — mapuje na "backend" string w chat clientcie.
/// Większość to OpenAI-compatible; <see cref="Anthropic"/> ma własny format (w MVP mamy tylko OpenAI-compat).
/// </summary>
public enum LlmProviderKind
{
    /// <summary>Ollama — lokalny OpenAI-compat (domyślnie http://localhost:11434/v1).</summary>
    Ollama = 0,

    /// <summary>LM Studio — lokalny OpenAI-compat (domyślnie http://localhost:1234/v1).</summary>
    LmStudio = 1,

    /// <summary>vLLM server — OpenAI-compat + rozszerzenia (guided_json).</summary>
    Vllm = 2,

    /// <summary>LocalAI — open-source OpenAI-compat.</summary>
    LocalAi = 3,

    /// <summary>OpenAI (cloud).</summary>
    OpenAI = 10,

    /// <summary>Azure OpenAI (cloud, inne headery ale OpenAI-compat body).</summary>
    AzureOpenAI = 11,

    /// <summary>Groq (cloud, OpenAI-compat).</summary>
    Groq = 12,

    /// <summary>Together AI (cloud, OpenAI-compat).</summary>
    TogetherAi = 13,

    /// <summary>Mistral AI (cloud, OpenAI-compat).</summary>
    MistralAi = 14,

    /// <summary>DeepSeek (cloud, OpenAI-compat).</summary>
    DeepSeek = 15,

    /// <summary>Dowolny endpoint OpenAI-compat którego nie ma na liście.</summary>
    OpenAiCompatible = 99
}

/// <summary>
/// Konfiguracja pojedynczego dostawcy LLM — URL, klucz, domyślny model. User może mieć wiele
/// providerów (np. Ollama lokalnie + OpenAI w chmurze + vLLM na serwerze firmowym) i wybierać
/// per-trigger który ma być użyty przez <c>VllmChecker</c>.
/// </summary>
public sealed class LlmProvider : Entity
{
    /// <summary>Wyświetlana nazwa, unikalna. Np. "Ollama lokalna (dev)", "GPT-4o cloud", "vLLM firmowy".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Krótki opis (skąd host, kto administruje, dla jakich zastosowań).</summary>
    public string? Description { get; set; }

    public LlmProviderKind Kind { get; set; } = LlmProviderKind.Ollama;

    /// <summary>Endpoint OpenAI-compat, z /v1 na końcu (np. http://host:11434/v1, https://api.openai.com/v1).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// API key. Dla lokalnych (Ollama, LM Studio) zwykle pusty. Dla chmury wymagany.
    /// Zapisywany w Mongo — rozważ szyfrowanie przez <c>ISecretCipher</c> (Faza 4 secrets).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Domyślny model używany gdy <c>VllmCheckConfig.ModelName</c> jest pusty.</summary>
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>Timeout HTTP w sekundach. Dla chmury 30-60s, dla VLLM z ciężkim modelem 120s+.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Czy dostępny do wyboru w triggerach / playgroundzie.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Czy to "domyślny" provider — używany gdy trigger / playground nie podaje explicit <c>LlmProviderId</c>.
    /// Tylko jeden provider w systemie może być default. Seeder tworzy go przy pierwszym starcie z LlmOptions.
    /// </summary>
    public bool IsDefault { get; set; }
}
