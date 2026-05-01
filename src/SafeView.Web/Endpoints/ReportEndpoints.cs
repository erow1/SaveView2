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
            bool? thumbnails,
            ReportStyle? style,
            IReportService reports,
            CancellationToken ct) =>
        {
            // ASP.NET parser z URL query oddaje DateTime z Kind=Unspecified gdy nie ma "Z" sufix.
            // Wymuszamy UTC żeby filtry MongoDB nie traktowały wartości jak Local i nie szyfowały.
            var f = NormalizeUtc(from ?? DateTime.UtcNow.AddDays(-7));
            var t = NormalizeUtc(to ?? DateTime.UtcNow);
            var filter = new ReportFilter(f, t, minSeverity, cameraId, thumbnails == true,
                style ?? ReportStyle.Graphic);

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

    private static DateTime NormalizeUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc         => dt,
        DateTimeKind.Local       => dt.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        _ => dt
    };
}
