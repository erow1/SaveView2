namespace SafeView.LLM;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Backend: "openai", "vllm", "ollama", "lmstudio". Steruje extras (np. guided_json).</summary>
    public string Backend { get; set; } = "openai";

    /// <summary>Endpoint OpenAI-compatible (np. http://localhost:8000/v1, http://localhost:11434/v1).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000/v1";

    /// <summary>API key (opcjonalny, dla lokalnych backendów może być pusty).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Domyślny model (np. "gpt-4o-mini", "llama3.1:8b", "Qwen/Qwen2.5-7B-Instruct").</summary>
    public string DefaultModel { get; set; } = "gpt-4o-mini";

    public int TimeoutSeconds { get; set; } = 60;
}
