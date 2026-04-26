using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;

namespace SafeView.Web.Hubs;

/// <summary>
/// SignalR hub dla strony /flow — broadcast-uje eventy runtime pipeline'u
/// (snapshot captured, trigger fired, action executed, VLLM rejected).
/// Wymaga auth — clients muszą mieć permisję cameras:view.
/// </summary>
[Authorize(Policy = "perm:cameras:view")]
public sealed class FlowHub : Hub
{
    // Brak server-side metod — to pure broadcast hub, serwer tylko push-uje.
}

/// <summary>
/// Publisher eventów flow-u — thin wrapper nad <see cref="IHubContext{TFlowHub}"/>.
/// Fire-and-forget: failure w broadcasts nie blokuje pipeline'u.
/// </summary>
public sealed class SignalRFlowEventPublisher : IFlowEventPublisher
{
    private readonly IHubContext<FlowHub> _hub;
    private readonly ILogger<SignalRFlowEventPublisher> _log;

    public SignalRFlowEventPublisher(IHubContext<FlowHub> hub, ILogger<SignalRFlowEventPublisher> log)
    {
        _hub = hub;
        _log = log;
    }

    public Task SnapshotCapturedAsync(string cameraId, CancellationToken ct = default)
        => SendAsync("SnapshotCaptured", new { cameraId }, ct);

    public Task TriggerFiredAsync(string triggerId, string zoneId, string cameraId, int detectionCount, CancellationToken ct = default)
        => SendAsync("TriggerFired", new { triggerId, zoneId, cameraId, detectionCount }, ct);

    public Task VllmRejectedAsync(string triggerId, string zoneId, string cameraId, CancellationToken ct = default)
        => SendAsync("VllmRejected", new { triggerId, zoneId, cameraId }, ct);

    public Task ActionExecutedAsync(string actionId, string triggerId, string status, int durationMs, CancellationToken ct = default)
        => SendAsync("ActionExecuted", new { actionId, triggerId, status, durationMs }, ct);

    private async Task SendAsync(string eventName, object payload, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(eventName, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never throw to caller — flow hub is non-critical diagnostic.
            _log.LogDebug(ex, "FlowHub send failed for {Event}", eventName);
        }
    }
}
