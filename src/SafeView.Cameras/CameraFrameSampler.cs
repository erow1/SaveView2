using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.Media;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Cameras;

namespace SafeView.Cameras;

/// <summary>
/// BackgroundService — okresowo wykonuje snapshoty dla każdej aktywnej kamery
/// zgodnie z jej <see cref="Camera.SnapshotIntervalSeconds"/>. Wyniki powiadamia
/// wszystkie zarejestrowane <see cref="IFrameObserver"/>.
///
/// Projekt: pojedyncza pętla, równoległe capture kontrolowane SemaphoreSlim.
/// Skala: dobre do ~kilkudziesięciu kamer. Przy większej liczbie → shardowanie.
/// </summary>
public sealed class CameraFrameSampler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISnapshotService _snapshots;
    private readonly IOptionsMonitor<CameraSamplerOptions> _opts;
    private readonly ILogger<CameraFrameSampler> _log;

    // Per-camera stan: ostatni udany timestamp (do wyznaczenia "due")
    private readonly Dictionary<string, DateTime> _lastCapture = new(StringComparer.Ordinal);

    public CameraFrameSampler(
        IServiceScopeFactory scopes,
        ISnapshotService snapshots,
        IOptionsMonitor<CameraSamplerOptions> opts,
        ILogger<CameraFrameSampler> log)
    {
        _scopes = scopes;
        _snapshots = snapshots;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _opts.CurrentValue;
        if (!opts.Enabled)
        {
            _log.LogInformation("CameraFrameSampler disabled via config.");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(opts.StartupDelaySeconds), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation("CameraFrameSampler started (per-camera interval, max concurrency {N})",
            opts.MaxConcurrentCaptures);

        var semaphore = new SemaphoreSlim(Math.Max(1, opts.MaxConcurrentCaptures));

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan sleep;
            try
            {
                sleep = await TickAsync(semaphore, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sampler tick failed");
                sleep = TimeSpan.FromSeconds(1);
            }

            try { await Task.Delay(sleep, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("CameraFrameSampler stopping.");
    }

    /// <summary>
    /// Przetwarza wszystkie "due" kamery (każdą zgodnie z jej <see cref="Camera.SnapshotIntervalSeconds"/>)
    /// i zwraca czas do następnego budzenia — równy najbliższemu terminowi kolejnej kamery,
    /// ograniczony dołem przez <see cref="CameraSamplerOptions.MinTickSeconds"/>.
    /// </summary>
    private async Task<TimeSpan> TickAsync(SemaphoreSlim semaphore, CancellationToken ct)
    {
        var minTick = TimeSpan.FromMilliseconds(Math.Max(100, _opts.CurrentValue.MinTickMilliseconds));

        List<Camera> cameras;
        using (var scope = _scopes.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ICameraRepository>();
            cameras = (await repo.ListEnabledAsync(ct).ConfigureAwait(false)).ToList();
        }

        if (cameras.Count == 0) return TimeSpan.FromSeconds(5);

        var now = DateTime.UtcNow;
        var dueCameras = cameras.Where(c => IsDue(c, now)).ToList();

        if (dueCameras.Count > 0)
        {
            var tasks = dueCameras.Select(c => CaptureOneAsync(c, semaphore, ct)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // Policz najkrótszy czas do kolejnego "due" terminu po wszystkich kamerach
        var nowAfter = DateTime.UtcNow;
        var minWait = TimeSpan.MaxValue;
        foreach (var c in cameras)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, c.SnapshotIntervalSeconds));
            var nextDue = _lastCapture.TryGetValue(c.Id, out var last)
                ? last + interval
                : nowAfter; // jeszcze ani razu nie pobrane → natychmiast
            var wait = nextDue - nowAfter;
            if (wait < minWait) minWait = wait;
        }

        if (minWait < minTick) return minTick;
        if (minWait == TimeSpan.MaxValue) return TimeSpan.FromSeconds(5);
        return minWait;
    }

    private bool IsDue(Camera camera, DateTime now)
    {
        var interval = Math.Max(1, camera.SnapshotIntervalSeconds);
        if (!_lastCapture.TryGetValue(camera.Id, out var last))
            return true; // pierwszy raz — od razu
        return (now - last).TotalSeconds >= interval;
    }

    private async Task CaptureOneAsync(Camera camera, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _snapshots.CaptureAsync(camera, ct).ConfigureAwait(false);
            _lastCapture[camera.Id] = DateTime.UtcNow;

            using var scope = _scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICameraRepository>();

            if (result.Success)
            {
                camera.LastSnapshotAt = result.CapturedAt;
                camera.LastStatus = "OK";
                try { await repo.UpdateAsync(camera, ct).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to update camera {Id} after snapshot", camera.Id); }

                // Powiadom obserwatorów (ML, zones, …) — izolowane try/catch per obserwator
                var observers = scope.ServiceProvider.GetServices<IFrameObserver>();
                foreach (var obs in observers)
                {
                    try { await obs.OnFrameAsync(camera, result, ct).ConfigureAwait(false); }
                    catch (Exception ex) { _log.LogWarning(ex, "Frame observer {Type} failed for camera {Id}", obs.GetType().Name, camera.Id); }
                }
            }
            else
            {
                camera.LastStatus = result.ErrorMessage ?? "snapshot failed";
                try { await repo.UpdateAsync(camera, ct).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to update camera {Id} after failure", camera.Id); }
                _log.LogDebug("Snapshot failed for {Camera}: {Err}", camera.Name, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "CaptureOneAsync unexpected failure for camera {Id}", camera.Id);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
