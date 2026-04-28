using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Persistence;

namespace SafeView.Infrastructure.Detection;

/// <summary>
/// Seeduje wbudowane klasy detekcji BHP przy każdym starcie (analog do
/// <c>PromptTemplateSeeder</c>). Identyfikacja przez stabilny <c>BuiltInKey</c> —
/// nie nadpisuje user-edits, pozwala dodawać nowe klasy w kolejnych wersjach bez migracji.
/// </summary>
public sealed class DetectionClassSeeder : IHostedService
{
    private readonly IDetectionClassRepository _repo;
    private readonly IMLModelRepository _modelRepo;
    private readonly ILogger<DetectionClassSeeder> _log;

    public DetectionClassSeeder(
        IDetectionClassRepository repo,
        IMLModelRepository modelRepo,
        ILogger<DetectionClassSeeder> log)
    {
        _repo = repo;
        _modelRepo = modelRepo;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Build model name → ID map dla ClosedSetBinding klas (np. coco-person, coco-cell-phone).
            // Hosted services są startowane sekwencyjnie więc ModelSeeder już zakończył.
            var allModels = await _modelRepo.ListAsync(cancellationToken).ConfigureAwait(false);
            var modelIdByName = allModels.ToDictionary(m => m.Name, m => m.Id, StringComparer.Ordinal);
            string? Resolve(string name) => modelIdByName.TryGetValue(name, out var id) ? id : null;

            int added = 0;
            foreach (var klass in BuiltInDetectionClasses.All(Resolve))
            {
                var existing = await _repo.GetByBuiltInKeyAsync(klass.BuiltInKey!, cancellationToken).ConfigureAwait(false);
                if (existing is not null) continue;
                await _repo.InsertAsync(klass, cancellationToken).ConfigureAwait(false);
                added++;
            }
            if (added > 0)
                _log.LogInformation("DetectionClassSeeder: zarejestrowano {N} nowych wbudowanych klas detekcji", added);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DetectionClassSeeder failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
