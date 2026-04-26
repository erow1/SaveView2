using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.Notifications;
using SafeView.Domain.Incidents;

namespace SafeView.Notifications;

public sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly IOptionsMonitor<NotificationOptions> _opts;
    private readonly SmtpEmailSender _smtp;
    private readonly WebhookSender _webhook;
    private readonly ILogger<NotificationDispatcher> _log;

    public NotificationDispatcher(
        IOptionsMonitor<NotificationOptions> opts,
        SmtpEmailSender smtp,
        WebhookSender webhook,
        ILogger<NotificationDispatcher> log)
    {
        _opts = opts;
        _smtp = smtp;
        _webhook = webhook;
        _log = log;
    }

    public async Task DispatchIncidentAsync(Incident incident, CancellationToken ct = default)
    {
        var cfg = _opts.CurrentValue;
        if ((int)incident.Severity < cfg.MinSeverity)
            return;

        var tasks = new List<Task>();
        if (cfg.Smtp.Enabled) tasks.Add(_smtp.SendAsync(incident, ct));
        foreach (var hook in cfg.Webhooks)
            tasks.Add(_webhook.SendAsync(hook, incident, ct));

        if (tasks.Count == 0) return;

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "One or more notification channels failed."); }
    }
}
