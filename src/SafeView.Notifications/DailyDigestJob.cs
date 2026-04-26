using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Reports;
using SafeView.Domain.Incidents;

namespace SafeView.Notifications;

/// <summary>
/// Codzienny digest — o godzinie <see cref="DigestOptions.HourUtc"/> UTC wysyła
/// e-mail z PDF raportem incydentów z ostatnich 24 h. Brak SMTP / zerowa skrzynka odbiorców
/// → job no-op (kompletna fail-safe).
/// </summary>
public sealed class DailyDigestJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<NotificationOptions> _opts;
    private readonly ILogger<DailyDigestJob> _log;

    public DailyDigestJob(IServiceScopeFactory scopes, IOptionsMonitor<NotificationOptions> opts, ILogger<DailyDigestJob> log)
    {
        _scopes = scopes;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Drobne opóźnienie startu — dajemy systemowi czas na seed/Mongo.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _opts.CurrentValue;
            var nextUtc = NextRunUtc(DateTime.UtcNow, cfg.Digest.HourUtc, cfg.Digest.MinuteUtc);
            var delay = nextUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                _log.LogInformation("DailyDigestJob: next run at {When} UTC (in {Delay})", nextUtc, delay);
                try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            try
            {
                if (cfg.Digest.Enabled && cfg.Smtp.Enabled && cfg.Smtp.To.Count > 0)
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                else
                    _log.LogDebug("DailyDigestJob: skipped (disabled or no recipients).");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "DailyDigestJob: run failed");
            }

            // Przesuń się o co najmniej minutę, żeby pętla nie zapętliła się w tę samą sekundę.
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal static DateTime NextRunUtc(DateTime nowUtc, int hour, int minute)
    {
        var today = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, hour, minute, 0, DateTimeKind.Utc);
        return today > nowUtc ? today : today.AddDays(1);
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IIncidentRepository>();
        var reports = scope.ServiceProvider.GetRequiredService<IReportService>();
        var smtp = scope.ServiceProvider.GetRequiredService<SmtpEmailSender>();

        var until = DateTime.UtcNow;
        var from = until.AddHours(-24);

        var totals = await incidents.CountAsync(from: from, until: until, ct: ct).ConfigureAwait(false);
        var critical = await incidents.CountAsync(from: from, until: until,
            severity: IncidentSeverity.Critical, ct: ct).ConfigureAwait(false);
        var high = await incidents.CountAsync(from: from, until: until,
            severity: IncidentSeverity.High, ct: ct).ConfigureAwait(false);

        var pdf = await reports.GenerateIncidentsPdfAsync(
            new ReportFilter(from, until), ct).ConfigureAwait(false);

        var subject = $"[SafeView] Digest 24h — {totals} incydentów ({critical} CRIT, {high} HIGH)";
        var body = $"""
            Podsumowanie dobowe SafeView
            ────────────────────────────
            Okres:    {from:yyyy-MM-dd HH:mm} → {until:yyyy-MM-dd HH:mm} UTC
            Wszystkie incydenty: {totals}
              CRITICAL: {critical}
              HIGH:     {high}

            Pełny raport PDF w załączniku.
            """;

        await smtp.SendRawAsync(subject, body,
            attachments: [(pdf.FileName, pdf.ContentType, pdf.Content)],
            ct: ct).ConfigureAwait(false);

        _log.LogInformation("DailyDigestJob: digest sent ({Count} incidents).", totals);
    }
}
