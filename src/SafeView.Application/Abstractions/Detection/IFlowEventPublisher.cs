namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Publikuje zdarzenia runtime pipeline'u do subskrybentów (strona /flow, ewentualnie
/// zewnętrzne dashboardy). Implementacja w SafeView.Web używa SignalR Hub.
/// Fire-and-forget — publisher NIE blokuje pipeline'u ani nie rzuca wyjątków w górę.
/// </summary>
public interface IFlowEventPublisher
{
    Task SnapshotCapturedAsync(string cameraId, CancellationToken ct = default);
    Task TriggerFiredAsync(string triggerId, string zoneId, string cameraId, int detectionCount, CancellationToken ct = default);
    Task VllmRejectedAsync(string triggerId, string zoneId, string cameraId, CancellationToken ct = default);
    Task ActionExecutedAsync(string actionId, string triggerId, string status, int durationMs, CancellationToken ct = default);
}
