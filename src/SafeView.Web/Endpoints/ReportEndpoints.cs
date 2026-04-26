using Microsoft.AspNetCore.Authorization;
using SafeView.Application.Abstractions.Reports;
using SafeView.Domain.Incidents;

namespace SafeView.Web.Endpoints;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var grp = endpoints.MapGroup("/reports").RequireAuthorization("perm:reports:generate");

        grp.MapGet("/incidents.{format}", async (
            string format,
            DateTime? from,
            DateTime? to,
            string? cameraId,
            IncidentSeverity? minSeverity,
            IReportService reports,
            CancellationToken ct) =>
        {
            var f = from ?? DateTime.UtcNow.Date.AddDays(-7);
            var t = to ?? DateTime.UtcNow.Date.AddDays(1);
            var filter = new ReportFilter(f, t, minSeverity, cameraId);

            var report = format.ToLowerInvariant() switch
            {
                "pdf" => await reports.GenerateIncidentsPdfAsync(filter, ct),
                "csv" => await reports.GenerateIncidentsCsvAsync(filter, ct),
                _ => null
            };
            if (report is null) return Results.BadRequest("format must be 'pdf' or 'csv'");
            return Results.File(report.Content, report.ContentType, report.FileName);
        });

        return endpoints;
    }
}
