using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Llm;

namespace SafeView.Infrastructure.Llm;

/// <summary>
/// Przy pierwszym starcie (gdy kolekcja <c>llm_providers</c> jest pusta) tworzy jeden provider
/// "Default" z wartości z appsettings <c>Llm</c>. Backward-compat z Fazą 3 gdzie był jeden
/// globalny LLM config. User może potem zmienić/dodać nowe na /admin/llm-providers.
/// </summary>
public sealed class LlmProviderSeeder : IHostedService
{
    private readonly ILlmProviderRepository _repo;
    private readonly IConfiguration _config;
    private readonly ILogger<LlmProviderSeeder> _log;

    public LlmProviderSeeder(ILlmProviderRepository repo, IConfiguration config, ILogger<LlmProviderSeeder> log)
    {
        _repo = repo;
        _config = config;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _repo.ListAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Count > 0) return;

            var section = _config.GetSection("Llm");
            var backend = section["Backend"]?.ToLowerInvariant() ?? "openai";
            var kind = backend switch
            {
                "ollama" => LlmProviderKind.Ollama,
                "lmstudio" => LlmProviderKind.LmStudio,
                "vllm" => LlmProviderKind.Vllm,
                "openai" => LlmProviderKind.OpenAI,
                _ => LlmProviderKind.OpenAiCompatible
            };

            var provider = new LlmProvider
            {
                Name = "Default (from appsettings)",
                Description = "Automatycznie utworzony z sekcji Llm w konfiguracji. Edytuj albo dodaj kolejne na /admin/llm-providers.",
                Kind = kind,
                BaseUrl = section["BaseUrl"] ?? "http://localhost:11434/v1",
                ApiKey = section["ApiKey"],
                DefaultModel = section["DefaultModel"] ?? "qwen2.5-vl:7b",
                TimeoutSeconds = int.TryParse(section["TimeoutSeconds"], out var t) ? t : 60,
                Enabled = true,
                IsDefault = true
            };

            await _repo.InsertAsync(provider, cancellationToken).ConfigureAwait(false);
            _log.LogInformation("LlmProviderSeeder: utworzono domyślnego providera z appsettings ({Kind}, {Url})",
                kind, provider.BaseUrl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LlmProviderSeeder failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
