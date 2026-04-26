using SafeView.Domain.Incidents;

namespace SafeView.Application.Abstractions.Reports;

public sealed record ReportFilter(
    DateTime From,
    DateTime To,
    IncidentSeverity? MinSeverity = null,
    string? CameraId = null);

public sealed record GeneratedReport(
    string FileName,
    string ContentType,
    byte[] Content);

public interface IReportService
{
    Task<GeneratedReport> GenerateIncidentsPdfAsync(ReportFilter filter, CancellationToken ct = default);
    Task<GeneratedReport> GenerateIncidentsCsvAsync(ReportFilter filter, CancellationToken ct = default);
}
