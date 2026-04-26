using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Vllm;

namespace SafeView.Infrastructure.Vllm;

/// <summary>
/// Seeduje wbudowane szablony VLLM dla typowych scenariuszy BHP przy każdym starcie.
///
/// Strategia:
///  • Identyfikacja przez stabilny <see cref="PromptTemplate.BuiltInKey"/> (slug)
///  • Jeśli szablon built-in istnieje — NIE nadpisujemy usera (user mógł go edytować,
///    kopiując jako własny). Nadpisanie tylko gdy metadata signature się zmieniła — ale to Faza D.
///  • Jeśli NIE istnieje — tworzymy. Pozwala dodawać nowe szablony w kolejnych wersjach bez migracji.
///
/// Każdy szablon zawiera:
///  • System prompt (EN) — role, guidelines, rubryka severity, anti-FP reguły
///  • User template (PL) — kontekst kamery/strefy z placeholderami
///  • JSON schema odpowiedzi — strict, spójny dla wszystkich szablonów
///  • Rekomendowany MinConfidence + DefaultMinSeverity + RequireGoodImageQuality
/// </summary>
public sealed class PromptTemplateSeeder : IHostedService
{
    private readonly IPromptTemplateRepository _repo;
    private readonly ILogger<PromptTemplateSeeder> _log;

    public PromptTemplateSeeder(IPromptTemplateRepository repo, ILogger<PromptTemplateSeeder> log)
    {
        _repo = repo;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            int added = 0;
            foreach (var t in BuiltInTemplates.All())
            {
                var existing = await _repo.GetByBuiltInKeyAsync(t.BuiltInKey!, cancellationToken).ConfigureAwait(false);
                if (existing is not null) continue;
                await _repo.InsertAsync(t, cancellationToken).ConfigureAwait(false);
                added++;
            }
            if (added > 0)
                _log.LogInformation("PromptTemplateSeeder: zarejestrowano {N} nowych wbudowanych szablonów VLLM", added);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PromptTemplateSeeder failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
