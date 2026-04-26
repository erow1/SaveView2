using SafeView.Application.Abstractions.Diagnostics;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Endpoint dla dashboardu wydajności (Faza 5 — performance monitoring).
/// Zwraca snapshot rolling-window metryk z DetectionPipeline.
/// Zabezpieczony przez <c>perm:admin:system-events</c> — reuse istniejącej permisji
/// (dashboard jest w kategorii "diagnostics" razem z /admin/system-events).
/// </summary>
public static class PerformanceEndpoints
{
    public static IEndpointRouteBuilder MapPerformanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/metrics/performance", (
            IPerformanceMetrics metrics,
            int? windowMinutes) =>
        {
            var window = TimeSpan.FromMinutes(Math.Clamp(windowMinutes ?? 5, 1, 15));
            var snapshot = metrics.GetSnapshot(window);
            return Results.Ok(snapshot);
        })
        .RequireAuthorization("perm:admin:system-events")
        .WithTags("Diagnostics");

        return endpoints;
    }
}
