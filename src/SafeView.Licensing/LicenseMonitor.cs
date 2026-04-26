using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SafeView.Licensing;

/// <summary>
/// Periodycznie przeładowuje plik licencji — wykrywa ręczną podmianę, wygaśnięcie, uszkodzenie.
/// </summary>
public sealed class LicenseMonitor : BackgroundService
{
    private readonly ILicenseService _license;
    private readonly IOptionsMonitor<LicenseOptions> _options;
    private readonly ILogger<LicenseMonitor> _log;

    public LicenseMonitor(
        ILicenseService license,
        IOptionsMonitor<LicenseOptions> options,
        ILogger<LicenseMonitor> log)
    {
        _license = license;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Pierwsze ładowanie natychmiast przy starcie
        await _license.LoadAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = Math.Max(1, _options.CurrentValue.RevalidateIntervalMinutes);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                await _license.LoadAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LicenseMonitor revalidation failed");
            }
        }
    }
}
