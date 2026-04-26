using SafeView.Application.Abstractions.Persistence;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Statystyki jakości szablonów VLLM — konsumowane przez /admin/vllm-templates.
/// Wynik: liczba wypaleń w oknie, liczba oznaczeń jako false-positive, FP rate, avg confidence,
/// timestamp ostatniego wypalenia.
/// </summary>
public static class VllmStatsEndpoint
{
    public static IEndpointRouteBuilder MapVllmStatsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/vllm/templates/{templateId}/stats", async (
            string templateId,
            int? windowDays,
            IIncidentRepository incidents,
            CancellationToken ct) =>
        {
            var days = Math.Clamp(windowDays ?? 30, 1, 365);
            var from = DateTime.UtcNow.AddDays(-days);
            var stats = await incidents.GetStatsByVllmTemplateAsync(templateId, from, ct);
            return Results.Ok(new
            {
                templateId,
                windowDays = days,
                fireCount = stats.FireCount,
                falsePositiveCount = stats.FalsePositiveCount,
                falsePositiveRate = stats.FalsePositiveRate,
                avgConfidence = stats.AvgConfidence,
                lastFireAt = stats.LastFireAt,
                confidenceHistogram = stats.ConfidenceHistogram
            });
        })
        .RequireAuthorization("perm:llm:configure")
        .WithTags("VLLM");

        return endpoints;
    }
}
