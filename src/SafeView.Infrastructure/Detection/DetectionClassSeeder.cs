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
    private readonly ILogger<DetectionClassSeeder> _log;

    public DetectionClassSeeder(IDetectionClassRepository repo, ILogger<DetectionClassSeeder> log)
    {
        _repo = repo;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            int added = 0;
            foreach (var klass in BuiltInDetectionClasses.All())
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
