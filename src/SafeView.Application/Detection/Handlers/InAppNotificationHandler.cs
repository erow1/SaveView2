using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Notifications;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection.Handlers;

/// <summary>
/// <see cref="ActionType.InAppNotification"/> handler — publikuje notyfikację przez
/// <see cref="IInAppNotificationBroker"/>, MainLayout pokazuje ją jako MudSnackbar toast
/// u każdego aktywnie zalogowanego użytkownika. Dodatkowo loguje Information (→ system_events)
/// i audit przez ActionDispatcher (→ /actions/history).
///
/// Config keys (opcjonalne):
///  • <c>"template"</c> — szablon treści (placeholders {camera}, {zone}, {trigger})
///  • <c>"severity"</c> — "info" | "success" | "warning" | "error" (default "info")
/// </summary>
public sealed class InAppNotificationHandler : IActionHandler
{
    private readonly IInAppNotificationBroker _broker;
    private readonly ILogger<InAppNotificationHandler> _log;

    public ActionType Type => ActionType.InAppNotification;

    public InAppNotificationHandler(IInAppNotificationBroker broker, ILogger<InAppNotificationHandler> log)
    {
        _broker = broker;
        _log = log;
    }

    public Task HandleAsync(DetectionAction action, ActionContext context, CancellationToken ct = default)
    {
        var template = action.Config.TryGetValue("template", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : "Powiadomienie [{trigger}] — strefa '{zone}' / kamera '{camera}'";

        var rendered = template
            .Replace("{camera}", context.CameraName, StringComparison.Ordinal)
            .Replace("{zone}", context.ZoneName, StringComparison.Ordinal)
            .Replace("{trigger}", context.TriggerName, StringComparison.Ordinal);

        var severity = action.Config.TryGetValue("severity", out var s) ? s?.ToLowerInvariant() : null;
        var sev = severity switch
        {
            "success" => InAppNotificationSeverity.Success,
            "warning" => InAppNotificationSeverity.Warning,
            "error" => InAppNotificationSeverity.Error,
            _ => InAppNotificationSeverity.Info
        };

        _broker.Publish(new InAppNotificationMessage(rendered, sev, DateTime.UtcNow));
        _log.LogInformation("IN-APP {Message}", rendered);
        return Task.CompletedTask;
    }
}
