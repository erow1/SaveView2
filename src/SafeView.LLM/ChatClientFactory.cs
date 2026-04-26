using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.LLM;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Llm;

namespace SafeView.LLM;

/// <summary>
/// Fabryka klientów LLM — per <see cref="LlmProvider"/>. Tworzy dedykowany
/// <see cref="OpenAiCompatibleChatClient"/> z własnym <see cref="HttpClient"/> (BaseAddress,
/// ApiKey, Timeout), kompatybilnym backendem (kind → string "ollama/vllm/openai"). Cache per
/// providerId. Invalidate po UpdateAsync (CRUD page woła).
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory, IDisposable
{
    private readonly ILlmProviderRepository _repo;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<LlmOptions> _fallbackOpts;
    private readonly ConcurrentDictionary<string, IChatClient> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HttpClient> _httpClients = new(StringComparer.Ordinal);
    private const string DefaultKey = "__default__";

    public ChatClientFactory(
        ILlmProviderRepository repo,
        ILoggerFactory loggerFactory,
        IOptions<LlmOptions> fallbackOpts)
    {
        _repo = repo;
        _loggerFactory = loggerFactory;
        _fallbackOpts = fallbackOpts;
    }

    public async Task<IChatClient> GetForAsync(string? providerId, CancellationToken ct = default)
    {
        var key = string.IsNullOrWhiteSpace(providerId) ? DefaultKey : providerId;

        if (_cache.TryGetValue(key, out var cached)) return cached;

        LlmProvider? provider = null;
        if (key != DefaultKey)
        {
            provider = await _repo.GetByIdAsync(key, ct).ConfigureAwait(false);
            if (provider is null || !provider.Enabled)
            {
                // Fallback — invalidated / disabled provider → default
                return await GetForAsync(null, ct).ConfigureAwait(false);
            }
        }
        else
        {
            provider = await _repo.GetDefaultAsync(ct).ConfigureAwait(false);
        }

        var client = BuildFromProviderOrFallback(provider);
        _cache[key] = client;
        return client;
    }

    public void Invalidate(string providerId)
    {
        var key = string.IsNullOrWhiteSpace(providerId) ? DefaultKey : providerId;
        _cache.TryRemove(key, out _);
        if (_httpClients.TryRemove(key, out var http)) http.Dispose();
    }

    private OpenAiCompatibleChatClient BuildFromProviderOrFallback(LlmProvider? provider)
    {
        LlmOptions opts;
        string cacheKey;
        if (provider is not null)
        {
            opts = new LlmOptions
            {
                Backend = KindToBackend(provider.Kind),
                BaseUrl = provider.BaseUrl,
                ApiKey = provider.ApiKey,
                DefaultModel = provider.DefaultModel,
                TimeoutSeconds = provider.TimeoutSeconds
            };
            cacheKey = provider.Id;
        }
        else
        {
            opts = _fallbackOpts.Value;
            cacheKey = DefaultKey;
        }

        var http = new HttpClient();
        if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
        {
            http.BaseAddress = new Uri(opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/");
        }
        http.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.TimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        }

        _httpClients[cacheKey] = http;

        var logger = _loggerFactory.CreateLogger<OpenAiCompatibleChatClient>();
        return new OpenAiCompatibleChatClient(http, Options.Create(opts), logger);
    }

    private static string KindToBackend(LlmProviderKind kind) => kind switch
    {
        LlmProviderKind.Ollama => "ollama",
        LlmProviderKind.LmStudio => "lmstudio",
        LlmProviderKind.Vllm => "vllm",
        LlmProviderKind.OpenAI => "openai",
        LlmProviderKind.AzureOpenAI => "openai",
        LlmProviderKind.Groq => "openai",
        LlmProviderKind.TogetherAi => "openai",
        LlmProviderKind.MistralAi => "openai",
        LlmProviderKind.DeepSeek => "openai",
        LlmProviderKind.LocalAi => "openai",
        _ => "openai"
    };

    public void Dispose()
    {
        foreach (var http in _httpClients.Values) http.Dispose();
        _httpClients.Clear();
        _cache.Clear();
    }
}
