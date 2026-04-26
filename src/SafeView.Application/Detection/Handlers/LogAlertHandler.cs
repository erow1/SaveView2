using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection.Handlers;

/// <summary>
/// <see cref="ActionType.LogAlert"/> handler — zapisuje wpis Information do Serilog
/// (Console + plik). Celowo NIE używa LogWarning/Error, żeby nie zaśmiecać kolekcji
/// <c>system_events</c> (ta jest przeznaczona dla błędów technicznych systemu).
/// Historia detekcji trzymana jest osobno w <c>incidents</c> przez DetectionPipeline.
///
/// Config keys (opcjonalne):
///  • "template" — szablon komunikatu (placeholdery: {camera}, {zone}, {trigger}, {labels})
/// </summary>
public sealed class LogAlertHandler : IActionHandler
{
    private readonly ILogger<LogAlertHandler> _log;

    public ActionType Type => ActionType.LogAlert;

    public LogAlertHandler(ILogger<LogAlertHandler> log)
    {
        _log = log;
    }

    public Task HandleAsync(DetectionAction action, ActionContext context, CancellationToken ct = default)
    {
        var template = action.Config.TryGetValue("template", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : "Detection alert [{trigger}] in zone '{zone}' on camera '{camera}' ({detections} objects)";

        var labels = string.Join(", ", context.Detections.Select(d => d.Label).Distinct());

        var rendered = template
            .Replace("{camera}", context.CameraName, StringComparison.Ordinal)
            .Replace("{zone}", context.ZoneName, StringComparison.Ordinal)
            .Replace("{trigger}", context.TriggerName, StringComparison.Ordinal)
            .Replace("{labels}", labels, StringComparison.Ordinal)
            .Replace("{detections}", context.Detections.Count.ToString(), StringComparison.Ordinal);

        _log.LogInformation("ALERT {Message} | frame={Frame}", rendered, context.FrameSnapshotPath);
        return Task.CompletedTask;
    }
}
