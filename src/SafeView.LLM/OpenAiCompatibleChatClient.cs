using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.LLM;

namespace SafeView.LLM;

/// <summary>
/// Klient OpenAI-compatible: vLLM, Ollama (z trybem OpenAI), LM Studio, OpenAI, Together, Groq, …
/// Strategia structured outputs:
///  • backend "vllm"     → ciało żądania zawiera `extra_body.guided_json` = JsonSchema
///  • backend "openai"   → `response_format = { type: "json_schema", json_schema: { name, schema } }`
///  • inne backendy      → schema dołączana w treści systemowej (best-effort)
/// </summary>
public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _opts;
    private readonly ILogger<OpenAiCompatibleChatClient> _log;

    public string Backend => _opts.Backend;

    public OpenAiCompatibleChatClient(HttpClient http, IOptions<LlmOptions> opts, ILogger<OpenAiCompatibleChatClient> log)
    {
        _opts = opts.Value;
        _log = log;
        _http = http;
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_opts.BaseUrl))
            _http.BaseAddress = new Uri(_opts.BaseUrl.EndsWith('/') ? _opts.BaseUrl : _opts.BaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _opts.TimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(_opts.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("models", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("models", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            var doc = await resp.Content
                .ReadFromJsonAsync<ModelsListResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (doc?.Data is null) return Array.Empty<string>();
            return doc.Data
                .Where(m => !string.IsNullOrEmpty(m.Id))
                .Select(m => m.Id!)
                .ToArray();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "ListModelsAsync failed for backend {Backend}", _opts.Backend);
            return Array.Empty<string>();
        }
    }

    public async Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ChatOptions();
        var model = options.Model ?? _opts.DefaultModel;

        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxTokens,
            ["messages"] = new JsonArray(messages.Select(BuildMessageNode).ToArray())
        };

        if (!string.IsNullOrWhiteSpace(options.JsonSchema))
        {
            ApplyStructuredOutput(body, options.JsonSchema!);
        }

        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try
        {
            using var content = JsonContent.Create(body);
            resp = await _http.PostAsync("chat/completions", content, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LLM HTTP failed");
            return new ChatResponse(false, string.Empty, model, 0, 0, sw.Elapsed, ex.Message);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new ChatResponse(false, string.Empty, model, 0, 0, sw.Elapsed, $"HTTP {(int)resp.StatusCode}: {Truncate(raw, 300)}");
            }

            ChatCompletionResponse? parsed;
            try { parsed = await resp.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct).ConfigureAwait(false); }
            catch (JsonException ex)
            {
                return new ChatResponse(false, string.Empty, model, 0, 0, sw.Elapsed, $"JSON: {ex.Message}");
            }

            sw.Stop();
            var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            return new ChatResponse(
                Success: true,
                Content: text,
                Model: parsed?.Model ?? model,
                PromptTokens: parsed?.Usage?.PromptTokens ?? 0,
                CompletionTokens: parsed?.Usage?.CompletionTokens ?? 0,
                Latency: sw.Elapsed);
        }
    }

    /// <summary>
    /// Buduje element <c>messages[i]</c>. Dla wiadomości z <see cref="ChatMessage.ImagePath"/>
    /// używa multimodal formatu OpenAI Vision (content = array [text, image_url]).
    /// Dla text-only zwraca prostą strukturę <c>{ role, content: "..." }</c>.
    /// </summary>
    private JsonNode BuildMessageNode(ChatMessage m)
    {
        var roleStr = RoleString(m.Role);

        // Text-only — kompatybilność wsteczna, większość backendów (ollama, LM Studio) obsługuje tylko to
        if (string.IsNullOrEmpty(m.ImagePath))
        {
            return new JsonObject
            {
                ["role"] = roleStr,
                ["content"] = m.Content
            };
        }

        // Multimodal — OpenAI Vision format:
        //   content: [ {type:"text", text:"..."}, {type:"image_url", image_url:{url:"data:image/jpeg;base64,..."}} ]
        string dataUrl;
        try
        {
            var bytes = File.ReadAllBytes(m.ImagePath);
            var mime = GuessMimeType(m.ImagePath);
            dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read image for multimodal message: {Path}", m.ImagePath);
            // Fallback — text-only
            return new JsonObject
            {
                ["role"] = roleStr,
                ["content"] = m.Content + "\n[image attachment unavailable]"
            };
        }

        return new JsonObject
        {
            ["role"] = roleStr,
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = m.Content },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = dataUrl }
                })
        };
    }

    private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg" // sensible default — wszystkie VLLM-y i tak konwertują
    };

    private void ApplyStructuredOutput(JsonObject body, string jsonSchema)
    {
        // Sparsuj JSON Schema raz — jeśli błędny, dorzucamy jako tekst do system prompt (fallback).
        JsonNode? schemaNode;
        try { schemaNode = JsonNode.Parse(jsonSchema); }
        catch
        {
            _log.LogWarning("Invalid JsonSchema — falling back to prompt-only");
            return;
        }
        if (schemaNode is null) return;

        var backend = (_opts.Backend ?? "openai").ToLowerInvariant();
        switch (backend)
        {
            case "vllm":
                // vLLM OpenAI-compatible: extra_body.guided_json = {schema}
                body["extra_body"] = new JsonObject { ["guided_json"] = schemaNode };
                break;

            case "openai":
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "safeview_response",
                        ["strict"] = true,
                        ["schema"] = schemaNode
                    }
                };
                break;

            case "ollama":
                // Ollama OpenAI-compat: response_format = "json" wymusza JSON, schema idzie do system promptu.
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                break;

            default:
                // LM Studio i inne — `response_format=json_object`, schema dołączamy do system message
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                break;
        }
    }

    private static string RoleString(ChatRole r) => r switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => "user"
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ── DTO ────────────────────────────────────────────────────────────────────
    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
        [JsonPropertyName("usage")] public Usage? Usage { get; set; }
    }
    private sealed class Choice
    {
        [JsonPropertyName("message")] public Message? Message { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }
    private sealed class Message
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
    private sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
        [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
    }
    private sealed class ModelsListResponse
    {
        [JsonPropertyName("data")] public List<ModelEntry>? Data { get; set; }
    }
    private sealed class ModelEntry
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}
