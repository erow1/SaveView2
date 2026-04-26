using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.Persistence;

namespace SafeView.Cameras;

/// <summary>
/// Background-loop, który okresowo woła <see cref="MediaMtxManager.ReconcileAsync"/> —
/// regeneruje config + restartuje proces gdy lista kamer się zmieniła albo proces padł.
/// </summary>
public sealed class MediaMtxRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly MediaMtxManager _manager;
    private readonly IOptionsMonitor<MediaMtxOptions> _opts;
    private readonly ILogger<MediaMtxRunner> _log;

    public MediaMtxRunner(
        IServiceScopeFactory scopes,
        MediaMtxManager manager,
        IOptionsMonitor<MediaMtxOptions> opts,
        ILogger<MediaMtxRunner> log)
    {
        _scopes = scopes;
        _manager = manager;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // krótki delay startowy żeby Mongo zdążył podnieść się
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _opts.CurrentValue;
            try
            {
                if (opts.Enabled && opts.AutoStart)
                {
                    using var scope = _scopes.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<ICameraRepository>();
                    var cams = await repo.ListAsync(stoppingToken).ConfigureAwait(false);
                    await _manager.ReconcileAsync(cams, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await _manager.StopAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "MediaMtxRunner reconcile failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, opts.ReconcileIntervalSeconds)), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        await _manager.StopAsync().ConfigureAwait(false);
    }
}
