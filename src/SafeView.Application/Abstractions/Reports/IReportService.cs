using SafeView.Domain.Incidents;

namespace SafeView.Application.Abstractions.Reports;

/// <summary>Forma wizualna raportu PDF.</summary>
public enum ReportStyle
{
    /// <summary>Pełen design dla zarządu — duże KPI, kolorowe wykresy, opcjonalne miniaturki.</summary>
    Graphic = 0,
    /// <summary>Kompaktowy tekst+tabele, czarno-biały — do druku, archiwum, audytu.</summary>
    Compact = 1
}

public sealed record ReportFilter(
    DateTime From,
    DateTime To,
    IncidentSeverity? MinSeverity = null,
    string? CameraId = null,
    bool IncludeThumbnails = false,
    ReportStyle Style = ReportStyle.Graphic);

public sealed record GeneratedReport(
    string FileName,
    string ContentType,
    byte[] Content);

public interface IReportService
{
    Task<GeneratedReport> GenerateIncidentsPdfAsync(ReportFilter filter, CancellationToken ct = default);
    Task<GeneratedReport> GenerateIncidentsCsvAsync(ReportFilter filter, CancellationToken ct = default);
}
