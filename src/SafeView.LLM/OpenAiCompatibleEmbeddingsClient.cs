using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.LLM;

namespace SafeView.LLM;

/// <summary>
/// Klient embeddings kompatybilny z OpenAI API (<c>POST /v1/embeddings</c>).
/// Obsługuje batch (lista tekstów w jednym requeście) + opcjonalny parametr <c>dimensions</c>
/// (OpenAI 3.x — pozwala wymusić docelowy rozmiar wektora).
///
/// <para>Endpoint response format (OpenAI-compatible):</para>
/// <code>
/// { "data": [ { "embedding": [...], "index": 0 }, ... ], "model": "...", "usage": { "total_tokens": N } }
/// </code>
/// </summary>
public sealed class OpenAiCompatibleEmbeddingsClient : IEmbeddingsClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _opts;
    private readonly ILogger<OpenAiCompatibleEmbeddingsClient> _log;

    public string Backend => _opts.Backend;

    public OpenAiCompatibleEmbeddingsClient(HttpClient http, IOptions<LlmOptions> opts,
        ILogger<OpenAiCompatibleEmbeddingsClient> log)
    {
        _opts = opts.Value;
        _log = log;
        _http = http;
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_opts.BaseUrl))
            _http.BaseAddress = new Uri(_opts.BaseUrl.EndsWith('/') ? _opts.BaseUrl : _opts.BaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _opts.TimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(_opts.ApiKey) && _http.DefaultRequestHeaders.Authorization is null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
    }

    public async Task<EmbeddingsResponse> GetEmbeddingsAsync(
        IReadOnlyList<string> inputs,
        EmbeddingsOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return new EmbeddingsResponse(true, [], null, 0, TimeSpan.Zero);

        options ??= new EmbeddingsOptions();
        var model = options.Model ?? _opts.DefaultModel;

        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(inputs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        };
        if (options.Dimensions is { } dim && dim > 0)
            body["dimensions"] = dim;

        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try
        {
            using var content = JsonContent.Create(body);
            resp = await _http.PostAsync("embeddings", content, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Embeddings HTTP failed");
            return EmbeddingsResponse.Failed(ex.Message);
        }

        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            return EmbeddingsResponse.Failed($"HTTP {(int)resp.StatusCode}: {raw}");

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                return EmbeddingsResponse.Failed("Response nie zawiera 'data' array");

            // Data mogą przyjść poza kolejnością — sortujemy po `index` żeby zachować dopasowanie do inputs.
            var sorted = dataEl.EnumerateArray()
                .OrderBy(e => e.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : int.MaxValue)
                .ToList();

            var result = new float[sorted.Count][];
            for (int i = 0; i < sorted.Count; i++)
            {
                if (!sorted[i].TryGetProperty("embedding", out var embEl) || embEl.ValueKind != JsonValueKind.Array)
                {
                    result[i] = [];
                    continue;
                }
                var vec = new float[embEl.GetArrayLength()];
                int j = 0;
                foreach (var v in embEl.EnumerateArray())
                    vec[j++] = v.GetSingle();
                result[i] = vec;
            }

            int tokens = 0;
            if (root.TryGetProperty("usage", out var usageEl)
                && usageEl.TryGetProperty("total_tokens", out var tokEl)
                && tokEl.TryGetInt32(out var t)) tokens = t;

            var respModel = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : model;
            return new EmbeddingsResponse(true, result, respModel, tokens, sw.Elapsed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Embeddings response parse failed");
            return EmbeddingsResponse.Failed("Parse error: " + ex.Message);
        }
    }
}
