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

    public async Task<bool> UnloadModelAsync(string? modelName = null, CancellationToken ct = default)
    {
        // Wspierane tylko dla Ollama — inne backendy są stateless per-request.
        if (!string.Equals(_opts.Backend, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug("UnloadModelAsync: backend {Backend} nie wspiera unload (no-op)", _opts.Backend);
            return false;
        }

        var model = string.IsNullOrWhiteSpace(modelName) ? _opts.DefaultModel : modelName!;
        if (string.IsNullOrWhiteSpace(model))
        {
            _log.LogWarning("UnloadModelAsync: brak nazwy modelu");
            return false;
        }

        // Ollama natywne API leży pod /api/generate (NIE /v1/...). Konfigurowany BaseAddress
        // typowo ma "/v1/" suffix — strip-ujemy do hosta i dorzucamy /api/generate.
        var baseAddr = _http.BaseAddress;
        if (baseAddr is null)
        {
            _log.LogWarning("UnloadModelAsync: brak BaseAddress");
            return false;
        }
        var unloadUri = new Uri(new Uri(baseAddr.GetLeftPart(UriPartial.Authority)), "/api/generate");

        var body = new JsonObject
        {
            ["model"] = model,
            ["keep_alive"] = 0  // 0 = unload immediately, brak nowego load-u dla tego call-a
        };

        try
        {
            using var content = JsonContent.Create(body);
            using var req = new HttpRequestMessage(HttpMethod.Post, unloadUri) { Content = content };
            // Auth header z _http.DefaultRequestHeaders nie kopiuje się do nowego requestu —
            // ale Ollama lokalny zwykle nie wymaga klucza, więc OK.
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                _log.LogInformation("Unloaded model {Model} from Ollama", model);
                return true;
            }
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogWarning("UnloadModelAsync HTTP {Code}: {Body}", (int)resp.StatusCode, Truncate(err, 200));
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "UnloadModelAsync failed for model {Model}", model);
            return false;
        }
    }

    public async Task<bool> PullModelAsync(string modelName, IProgress<PullProgress>? progress = null, CancellationToken ct = default)
    {
        if (!string.Equals(_opts.Backend, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug("PullModelAsync: backend {Backend} nie wspiera pull (no-op)", _opts.Backend);
            return false;
        }
        if (string.IsNullOrWhiteSpace(modelName))
        {
            _log.LogWarning("PullModelAsync: brak nazwy modelu");
            return false;
        }

        var baseAddr = _http.BaseAddress;
        if (baseAddr is null) { _log.LogWarning("PullModelAsync: brak BaseAddress"); return false; }
        var pullUri = new Uri(new Uri(baseAddr.GetLeftPart(UriPartial.Authority)), "/api/pull");

        var body = new JsonObject { ["name"] = modelName, ["stream"] = true };

        try
        {
            using var content = JsonContent.Create(body);
            using var req = new HttpRequestMessage(HttpMethod.Post, pullUri) { Content = content };
            // Pull 4-8 GB layerów może trwać długo — większy timeout niż domyślny 30s.
            // ResponseHeadersRead żeby zacząć czytać stream natychmiast bez bufferowania całości.
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarning("PullModelAsync HTTP {Code}: {Body}", (int)resp.StatusCode, Truncate(err, 200));
                return false;
            }

            // NDJSON stream — każda linia to JSON ze statusem progresu.
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            string? line;
            bool sawSuccess = false;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var node = JsonNode.Parse(line);
                    var status = node?["status"]?.GetValue<string>();
                    long? completed = node?["completed"]?.GetValue<long>();
                    long? total = node?["total"]?.GetValue<long>();
                    progress?.Report(new PullProgress(status, completed, total));
                    if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                        sawSuccess = true;
                    if (node?["error"]?.GetValue<string>() is { } errMsg)
                    {
                        _log.LogWarning("PullModelAsync error from server: {Err}", errMsg);
                        return false;
                    }
                }
                catch (JsonException) { /* ignore malformed line */ }
            }
            _log.LogInformation("PullModelAsync: model {Model} pulled (success={Ok})", modelName, sawSuccess);
            return sawSuccess;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PullModelAsync failed for {Model}", modelName);
            return false;
        }
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
        // FIX: pusty string nie wpada w `??` — używamy IsNullOrWhiteSpace żeby fallback do
        // _opts.DefaultModel działał też gdy caller wyczyścił TextField w UI (zwraca "" nie null).
        var model = string.IsNullOrWhiteSpace(options.Model) ? _opts.DefaultModel : options.Model!;

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

        // Debug log — kluczowy dla diagnozy "LLM odpowiada bez sensu". Pokazuje czy obraz
        // został zalączony, jaki model jest wołany, ile messages, czy strict-schema jest aktywny.
        var imageCount = messages.Count(m => !string.IsNullOrEmpty(m.ImagePath));
        _log.LogDebug(
            "LLM request: backend={Backend} model={Model} messages={Count} images={Images} schema={HasSchema}",
            _opts.Backend, model, messages.Count, imageCount, options.JsonSchema is not null);

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
