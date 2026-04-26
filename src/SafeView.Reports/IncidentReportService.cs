using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Reports;
using SafeView.Domain.Incidents;

namespace SafeView.Reports;

public sealed class IncidentReportService : IReportService
{
    private readonly IIncidentRepository _incidents;

    static IncidentReportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public IncidentReportService(IIncidentRepository incidents)
    {
        _incidents = incidents;
    }

    public async Task<GeneratedReport> GenerateIncidentsPdfAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var data = await LoadAsync(filter, ct).ConfigureAwait(false);
        var doc = BuildPdf(filter, data);
        var bytes = doc.GeneratePdf();
        var name = $"safeview_incidents_{filter.From:yyyyMMdd}_{filter.To:yyyyMMdd}.pdf";
        return new GeneratedReport(name, "application/pdf", bytes);
    }

    public async Task<GeneratedReport> GenerateIncidentsCsvAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var data = await LoadAsync(filter, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine("OccurredAt;Camera;Category;Severity;Status;Summary;ModelName;FrameRelativePath");
        foreach (var i in data)
        {
            sb.Append(i.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(Csv(i.CameraName)).Append(';');
            sb.Append(Csv(i.Category)).Append(';');
            sb.Append(i.Severity).Append(';');
            sb.Append(i.Status).Append(';');
            sb.Append(Csv(i.Summary)).Append(';');
            sb.Append(Csv(i.ModelName ?? string.Empty)).Append(';');
            sb.Append(Csv(i.FrameRelativePath ?? string.Empty));
            sb.AppendLine();
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        var name = $"safeview_incidents_{filter.From:yyyyMMdd}_{filter.To:yyyyMMdd}.csv";
        return new GeneratedReport(name, "text/csv; charset=utf-8", bytes);
    }

    private async Task<List<Incident>> LoadAsync(ReportFilter f, CancellationToken ct)
    {
        var all = await _incidents.ListByDateRangeAsync(f.From, f.To, ct).ConfigureAwait(false);
        IEnumerable<Incident> q = all;
        if (f.MinSeverity is not null) q = q.Where(i => i.Severity >= f.MinSeverity.Value);
        if (!string.IsNullOrEmpty(f.CameraId)) q = q.Where(i => i.CameraId == f.CameraId);
        return q.ToList();
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.IndexOfAny([';', '"', '\n', '\r']) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // ── PDF (QuestPDF Fluent) ─────────────────────────────────────────────────
    private static QuestPDF.Fluent.Document BuildPdf(ReportFilter f, List<Incident> data)
    {
        var groupedByDay = data
            .GroupBy(i => i.OccurredAt.Date)
            .OrderBy(g => g.Key)
            .ToList();
        var bySeverity = data.GroupBy(i => i.Severity).ToDictionary(g => g.Key, g => g.Count());
        var byCategory = data.GroupBy(i => i.Category).OrderByDescending(g => g.Count()).Take(10).ToList();

        return QuestPDF.Fluent.Document.Create(doc =>
        {
            doc.Page(p =>
            {
                p.Size(PageSizes.A4);
                p.Margin(28);
                p.DefaultTextStyle(t => t.FontSize(10).FontFamily("Helvetica"));

                p.Header().Column(col =>
                {
                    col.Item().Text("SafeView — Raport incydentów").FontSize(18).Bold();
                    col.Item().Text($"Okres: {f.From:yyyy-MM-dd} → {f.To:yyyy-MM-dd}    ·    Wygenerowano: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                p.Content().PaddingVertical(8).Column(col =>
                {
                    col.Spacing(10);

                    // Podsumowanie
                    col.Item().Text("Podsumowanie").FontSize(13).Bold();
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Background(Colors.Grey.Lighten4).Padding(8).Column(c =>
                        {
                            c.Item().Text("Łącznie").FontSize(9).FontColor(Colors.Grey.Darken1);
                            c.Item().Text(data.Count.ToString()).FontSize(20).Bold();
                        });
                        foreach (var s in new[] {
                            IncidentSeverity.Critical, IncidentSeverity.High,
                            IncidentSeverity.Medium, IncidentSeverity.Low })
                        {
                            row.RelativeItem().Background(Colors.Grey.Lighten4).Padding(8).Column(c =>
                            {
                                c.Item().Text(s.ToString()).FontSize(9).FontColor(Colors.Grey.Darken1);
                                c.Item().Text(bySeverity.TryGetValue(s, out var n) ? n.ToString() : "0").FontSize(20).Bold();
                            });
                        }
                    });

                    // Top kategorie
                    col.Item().PaddingTop(6).Text("Top kategorie").FontSize(13).Bold();
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(1); });
                        t.Header(h =>
                        {
                            h.Cell().Element(HeaderCell).Text("Kategoria");
                            h.Cell().Element(HeaderCell).AlignRight().Text("Liczba");
                        });
                        foreach (var g in byCategory)
                        {
                            t.Cell().Element(BodyCell).Text(g.Key);
                            t.Cell().Element(BodyCell).AlignRight().Text(g.Count().ToString());
                        }
                    });

                    // Dziennik dzienny
                    col.Item().PaddingTop(6).Text("Rozkład dzienny").FontSize(13).Bold();
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(1); c.RelativeColumn(1); c.RelativeColumn(1); c.RelativeColumn(1); });
                        t.Header(h =>
                        {
                            h.Cell().Element(HeaderCell).Text("Data");
                            h.Cell().Element(HeaderCell).AlignRight().Text("Łącznie");
                            h.Cell().Element(HeaderCell).AlignRight().Text("Critical");
                            h.Cell().Element(HeaderCell).AlignRight().Text("High");
                            h.Cell().Element(HeaderCell).AlignRight().Text("Medium");
                        });
                        foreach (var g in groupedByDay)
                        {
                            t.Cell().Element(BodyCell).Text(g.Key.ToString("yyyy-MM-dd"));
                            t.Cell().Element(BodyCell).AlignRight().Text(g.Count().ToString());
                            t.Cell().Element(BodyCell).AlignRight().Text(g.Count(i => i.Severity == IncidentSeverity.Critical).ToString());
                            t.Cell().Element(BodyCell).AlignRight().Text(g.Count(i => i.Severity == IncidentSeverity.High).ToString());
                            t.Cell().Element(BodyCell).AlignRight().Text(g.Count(i => i.Severity == IncidentSeverity.Medium).ToString());
                        }
                    });

                    // Lista incydentów
                    col.Item().PaddingTop(6).Text("Incydenty").FontSize(13).Bold();
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(110); // when
                            c.RelativeColumn(2);   // camera
                            c.RelativeColumn(2);   // category
                            c.ConstantColumn(60);  // severity
                            c.RelativeColumn(4);   // summary
                        });
                        t.Header(h =>
                        {
                            h.Cell().Element(HeaderCell).Text("Kiedy");
                            h.Cell().Element(HeaderCell).Text("Kamera");
                            h.Cell().Element(HeaderCell).Text("Kategoria");
                            h.Cell().Element(HeaderCell).Text("Ważność");
                            h.Cell().Element(HeaderCell).Text("Podsumowanie");
                        });
                        foreach (var i in data)
                        {
                            t.Cell().Element(BodyCell).Text(i.OccurredAt.ToString("MM-dd HH:mm"));
                            t.Cell().Element(BodyCell).Text(i.CameraName);
                            t.Cell().Element(BodyCell).Text(i.Category);
                            t.Cell().Element(BodyCell).Text(i.Severity.ToString());
                            t.Cell().Element(BodyCell).Text(i.Summary);
                        }
                    });
                });

                p.Footer().AlignCenter().Text(x =>
                {
                    x.Span("SafeView · ").FontSize(9).FontColor(Colors.Grey.Darken1);
                    x.CurrentPageNumber().FontSize(9).FontColor(Colors.Grey.Darken1);
                    x.Span(" / ").FontSize(9).FontColor(Colors.Grey.Darken1);
                    x.TotalPages().FontSize(9).FontColor(Colors.Grey.Darken1);
                });
            });
        });
    }

    private static IContainer HeaderCell(IContainer c) => c
        .Background(Colors.Grey.Lighten3).PaddingVertical(4).PaddingHorizontal(6).DefaultTextStyle(t => t.SemiBold().FontSize(9));

    private static IContainer BodyCell(IContainer c) => c
        .BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(6).DefaultTextStyle(t => t.FontSize(9));
}
