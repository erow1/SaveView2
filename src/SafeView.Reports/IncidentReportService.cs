using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Reports;
using SafeView.Application.Abstractions.Storage;
using SafeView.Domain.Incidents;

namespace SafeView.Reports;

public sealed class IncidentReportService : IReportService
{
    private readonly IIncidentRepository _incidents;
    private readonly IFileStore _files;

    static IncidentReportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // Aplikacja chodzi jako root i ma w PATH np. /Karta1T storage z setkami tysięcy klatek;
        // domyślny systemowy skan czcionek QuestPDF (UseEnvironmentFonts=true) trafia w limit
        // 100 000 plików i rzuca TypeInitializationException. Bundlujemy wszystko offline-first,
        // używamy wbudowanych fontów QuestPDF (Lato) — zero zależności od systemu.
        QuestPDF.Settings.UseEnvironmentFonts = false;
        QuestPDF.Settings.FontDiscoveryPaths.Clear();
        // Debug informacje w wyjątkach — bez tego DocumentLayoutException nie wskazuje, który
        // element się nie zmieścił. Narzut akceptowalny: PDF nie jest hot path.
        QuestPDF.Settings.EnableDebugging = true;
    }

    public IncidentReportService(IIncidentRepository incidents, IFileStore files)
    {
        _incidents = incidents;
        _files = files;
    }

    public async Task<GeneratedReport> GenerateIncidentsPdfAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var data = await LoadAsync(filter, ct).ConfigureAwait(false);

        // Pre-load thumbnails (tylko gdy graficzny + user poprosił) — IO przed Document.Create,
        // bo render PDF jest sync a IFileStore jest async.
        var thumbnails = new Dictionary<string, byte[]>();
        if (filter.IncludeThumbnails && filter.Style == ReportStyle.Graphic)
        {
            foreach (var i in data)
            {
                if (string.IsNullOrEmpty(i.FrameRelativePath)) continue;
                try
                {
                    await using var s = await _files.OpenReadAsync(FileKind.Frame, i.FrameRelativePath, ct).ConfigureAwait(false);
                    if (s is null) continue;
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms, ct).ConfigureAwait(false);
                    thumbnails[i.Id] = ms.ToArray();
                }
                catch { /* brak klatki dowodowej — pomijamy */ }
            }
        }

        var doc = filter.Style == ReportStyle.Compact
            ? BuildPdfCompact(filter, data)
            : BuildPdf(filter, data, thumbnails);
        var bytes = doc.GeneratePdf();
        var styleSuffix = filter.Style == ReportStyle.Compact ? "_compact" : "";
        var name = $"safeview_incidents_{filter.From:yyyyMMdd}_{filter.To:yyyyMMdd}{styleSuffix}.pdf";
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

    // ── Paleta kolorów (light executive design) ────────────────────────────────
    private const string BrandAccent  = "#FFD600"; // SafeView yellow
    private const string InkPrimary   = "#1A1A1A"; // tekst tytułów
    private const string InkSecondary = "#5A5A5A"; // tekst pomocniczy
    private const string InkMuted     = "#9CA3AF";
    private const string Surface      = "#FFFFFF";
    private const string SurfaceAlt   = "#F8F9FB"; // subtelne tło sekcji
    private const string Hairline     = "#E5E7EB";

    private const string SevCritical = "#DC2626"; // red 600
    private const string SevHigh     = "#F97316"; // orange 600
    private const string SevMedium   = "#F5A524"; // amber
    private const string SevLow      = "#3B82F6"; // blue 500

    private static string SeverityColor(IncidentSeverity s) => s switch
    {
        IncidentSeverity.Critical => SevCritical,
        IncidentSeverity.High     => SevHigh,
        IncidentSeverity.Medium   => SevMedium,
        IncidentSeverity.Low      => SevLow,
        _ => InkMuted
    };

    // 8-kolorowa paleta dla kategorii (cyklicznie)
    private static readonly string[] CategoryPalette =
    [
        "#3B82F6", "#10B981", "#F59E0B", "#8B5CF6",
        "#EC4899", "#06B6D4", "#84CC16", "#F97316"
    ];

    // ── PDF (QuestPDF Fluent — light executive design) ────────────────────────
    private static QuestPDF.Fluent.Document BuildPdf(
        ReportFilter f,
        List<Incident> data,
        Dictionary<string, byte[]> thumbnails)
    {
        var generatedAt = DateTime.Now;
        var bySeverity = data.GroupBy(i => i.Severity).ToDictionary(g => g.Key, g => g.Count());
        var byCategory = data.GroupBy(i => string.IsNullOrEmpty(i.Category) ? "(brak)" : i.Category)
                             .OrderByDescending(g => g.Count()).Take(8).ToList();
        var byCamera   = data.GroupBy(i => string.IsNullOrEmpty(i.CameraName) ? "(brak)" : i.CameraName)
                             .OrderByDescending(g => g.Count()).Take(5).ToList();

        // Dni do wykresu — agregujemy do max 30 słupków żeby każdy miał czytelną szerokość
        // na A4 (~520pt content / 30 = ~17pt per słupek). Dla okresów >30 dni grupujemy
        // po N dni; etykiety pokazują datę pierwszego dnia w bucket'cie.
        var fromDate = f.From.Date;
        var toDate   = f.To.Date >= fromDate ? f.To.Date : fromDate;
        var rawDaySpan = Math.Max(1, (int)(toDate - fromDate).TotalDays + 1);
        const int maxBars = 30;
        var bucketSize = (rawDaySpan + maxBars - 1) / maxBars; // ceil
        var barCount = (rawDaySpan + bucketSize - 1) / bucketSize;
        var dayBuckets = new int[barCount];
        var critByDay  = new int[barCount];
        foreach (var i in data)
        {
            var dayIdx = (int)(i.OccurredAt.Date - fromDate).TotalDays;
            if (dayIdx < 0 || dayIdx >= rawDaySpan) continue;
            var b = dayIdx / bucketSize;
            if (b < 0 || b >= barCount) continue;
            dayBuckets[b]++;
            if (i.Severity == IncidentSeverity.Critical) critByDay[b]++;
        }
        var dayMax = dayBuckets.Length == 0 ? 0 : dayBuckets.Max();

        return QuestPDF.Fluent.Document.Create(doc =>
        {
            doc.Page(p =>
            {
                p.Size(PageSizes.A4);
                p.Margin(36);
                p.PageColor(Surface);
                p.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Lato).FontColor(InkPrimary));

                p.Header().Element(c => HeaderBand(c, f, generatedAt));

                p.Content().PaddingTop(12).Column(col =>
                {
                    col.Spacing(14);

                    // ── 1. Hero / Executive summary ───────────────────────────
                    col.Item().Element(c => HeroSummary(c, data, bySeverity));

                    // ── 2. Severity rozkład (stacked bar 100%) + legenda ─────
                    col.Item().Element(c => SeverityDistribution(c, data, bySeverity));

                    // ── 3. Wykres dzienny (bar chart) ─────────────────────────
                    col.Item().Element(c => DailyTrendChart(c, fromDate, barCount, bucketSize, dayBuckets, critByDay, dayMax));

                    // ── 4. Top kategorie zagrożeń (horizontal bars) ──────────
                    col.Item().Element(c => CategoryBars(c, byCategory, data.Count));

                    // ── 5. Hotspoty — kamery z największą liczbą zdarzeń ─────
                    if (byCamera.Count > 0)
                        col.Item().Element(c => CameraHotspots(c, byCamera, data.Count));

                    // ── 6. Lista incydentów ───────────────────────────────────
                    // Każdy incydent jako osobny col.Item() — kluczowe dla page-breakingu.
                    // QuestPDF nie potrafi rozłamać pojedynczego Item-a wewnątrz Column,
                    // więc gdy lista ma 50+ pozycji, Column wyrasta poza stronę i rzuca
                    // DocumentLayoutException. Top-level Item-y są łamane między stronami.
                    col.Item().Element(c => IncidentsListHeader(c, data.Count, f.IncludeThumbnails));
                    if (data.Count == 0)
                    {
                        col.Item().PaddingTop(6).Text("Brak incydentów w wybranym okresie.")
                            .FontSize(9).FontColor(InkMuted);
                    }
                    else
                    {
                        var ordered = data.OrderByDescending(i => i.OccurredAt).ToList();
                        for (int i = 0; i < ordered.Count; i++)
                        {
                            var inc = ordered[i];
                            byte[]? thumb = null;
                            if (f.IncludeThumbnails) thumbnails.TryGetValue(inc.Id, out thumb);
                            col.Item().PaddingTop(i == 0 ? 6 : 0)
                                .Element(c => IncidentRow(c, inc, thumb, f.IncludeThumbnails));
                            col.Item().LineHorizontal(0.3f).LineColor(Hairline);
                        }
                    }
                });

                p.Footer().Element(FooterBand);
            });
        });
    }

    // ── Components ───────────────────────────────────────────────────────────

    private static void HeaderBand(IContainer c, ReportFilter f, DateTime generatedAt)
    {
        c.PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Row(r =>
                {
                    r.AutoItem().Background(BrandAccent).Width(4).Height(20);
                    r.AutoItem().PaddingLeft(8).AlignMiddle().Text("SAFEVIEW")
                        .FontSize(11).Bold().LetterSpacing(0.18f).FontColor(InkPrimary);
                });
                col.Item().PaddingTop(2).PaddingLeft(12).Text("Raport bezpieczeństwa przemysłowego")
                    .FontSize(9).FontColor(InkMuted);
            });
            row.AutoItem().AlignBottom().Text(t =>
            {
                t.AlignRight();
                t.Span($"Okres: ").FontSize(9).FontColor(InkMuted);
                t.Span($"{f.From:yyyy-MM-dd} → {f.To:yyyy-MM-dd}").FontSize(9).SemiBold().FontColor(InkPrimary);
                t.Line($"");
                t.Span("Wygenerowano: ").FontSize(9).FontColor(InkMuted);
                t.Span($"{generatedAt:yyyy-MM-dd HH:mm}").FontSize(9).SemiBold().FontColor(InkPrimary);
            });
        });
    }

    private static void FooterBand(IContainer c)
    {
        c.PaddingTop(8).BorderTop(0.5f).BorderColor(Hairline).PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Text("SafeView · poufne — do użytku wewnętrznego")
                .FontSize(8).FontColor(InkMuted);
            row.AutoItem().Text(x =>
            {
                x.Span("strona ").FontSize(8).FontColor(InkMuted);
                x.CurrentPageNumber().FontSize(8).FontColor(InkPrimary).SemiBold();
                x.Span(" / ").FontSize(8).FontColor(InkMuted);
                x.TotalPages().FontSize(8).FontColor(InkPrimary).SemiBold();
            });
        });
    }

    private static void HeroSummary(IContainer c, List<Incident> data, Dictionary<IncidentSeverity, int> bySeverity)
    {
        c.Column(col =>
        {
            col.Item().Text("Wykonawcze podsumowanie").FontSize(13).Bold().FontColor(InkPrimary);
            col.Item().PaddingTop(1).Text("Najważniejsze wskaźniki bezpieczeństwa za wybrany okres.")
                .FontSize(9).FontColor(InkSecondary);

            col.Item().PaddingTop(8).Row(row =>
            {
                row.Spacing(6);

                BigKpi(row, "Łącznie", data.Count.ToString(), InkPrimary, BrandAccent);
                BigKpi(row, "Krytyczne", (bySeverity.GetValueOrDefault(IncidentSeverity.Critical)).ToString(), SevCritical, SevCritical);
                BigKpi(row, "Wysokie",   (bySeverity.GetValueOrDefault(IncidentSeverity.High)).ToString(),     SevHigh,     SevHigh);
                BigKpi(row, "Średnie",   (bySeverity.GetValueOrDefault(IncidentSeverity.Medium)).ToString(),   SevMedium,   SevMedium);
                BigKpi(row, "Niskie",    (bySeverity.GetValueOrDefault(IncidentSeverity.Low)).ToString(),      SevLow,      SevLow);
            });
        });
    }

    private static void BigKpi(RowDescriptor row, string label, string value, string valueColor, string accent)
    {
        // Border-left jako akcent — light look: cienka linia zamiast tła, mniejsze cyfry
        row.RelativeItem().BorderLeft(2).BorderColor(accent)
            .PaddingVertical(6).PaddingHorizontal(8).Column(col =>
        {
            col.Item().Text(label).FontSize(8).FontColor(InkSecondary).LetterSpacing(0.3f);
            col.Item().PaddingTop(2).Text(value).FontSize(20).Bold().FontColor(valueColor);
        });
    }

    private static void SeverityDistribution(IContainer c, List<Incident> data, Dictionary<IncidentSeverity, int> bySeverity)
    {
        var total = Math.Max(1, data.Count);
        var segments = new (string Label, int Count, string Color)[]
        {
            ("Krytyczne", bySeverity.GetValueOrDefault(IncidentSeverity.Critical), SevCritical),
            ("Wysokie",   bySeverity.GetValueOrDefault(IncidentSeverity.High),     SevHigh),
            ("Średnie",   bySeverity.GetValueOrDefault(IncidentSeverity.Medium),   SevMedium),
            ("Niskie",    bySeverity.GetValueOrDefault(IncidentSeverity.Low),      SevLow),
        };

        c.Column(col =>
        {
            col.Item().Text("Rozkład ważności").FontSize(13).Bold().FontColor(InkPrimary);
            col.Item().PaddingTop(2).Text("Procentowy udział kategorii ważności w łącznej liczbie zdarzeń.")
                .FontSize(9).FontColor(InkSecondary);

            // Stacked bar 100%
            col.Item().PaddingTop(6).Height(12).Row(r =>
            {
                foreach (var seg in segments)
                {
                    if (seg.Count == 0) continue;
                    var w = seg.Count / (float)total;
                    r.RelativeItem(w).Background(seg.Color);
                }
                if (data.Count == 0)
                    r.RelativeItem().Background(Hairline);
            });

            // Legenda
            col.Item().PaddingTop(6).Row(r =>
            {
                r.Spacing(16);
                foreach (var seg in segments)
                {
                    var pct = data.Count == 0 ? 0 : seg.Count * 100.0 / data.Count;
                    r.AutoItem().Row(rr =>
                    {
                        rr.AutoItem().AlignMiddle().Width(10).Height(10).Background(seg.Color);
                        rr.AutoItem().PaddingLeft(6).AlignMiddle().Text(t =>
                        {
                            t.Span($"{seg.Label}  ").FontSize(9).FontColor(InkSecondary);
                            t.Span($"{seg.Count}").FontSize(10).Bold().FontColor(InkPrimary);
                            t.Span($"  ({pct:F0}%)").FontSize(9).FontColor(InkMuted);
                        });
                    });
                }
            });
        });
    }

    private static void DailyTrendChart(IContainer c, DateTime fromDate, int barCount, int bucketSize, int[] day, int[] crit, int max)
    {
        c.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("Trend dzienny").FontSize(13).Bold().FontColor(InkPrimary);
                row.AutoItem().AlignMiddle().Text(t =>
                {
                    t.Span(bucketSize == 1 ? "max/dzień: " : $"max/{bucketSize}d: ").FontSize(9).FontColor(InkMuted);
                    t.Span($"{max}").FontSize(10).Bold().FontColor(InkPrimary);
                });
            });
            col.Item().PaddingTop(2).Text("Liczba incydentów w okresie — czerwona część kolumny to zdarzenia krytyczne.")
                .FontSize(9).FontColor(InkSecondary);

            // Wykres słupkowy: container 80 wysokości, bary skalowane do max
            const float chartH = 80f;
            col.Item().PaddingTop(8).Height(chartH).Row(r =>
            {
                if (max == 0)
                {
                    r.RelativeItem().AlignCenter().AlignMiddle().Text("Brak danych w wybranym okresie.")
                        .FontSize(9).FontColor(InkMuted);
                    return;
                }
                for (int i = 0; i < barCount; i++)
                {
                    var v = day[i];
                    var c2 = crit[i];
                    var totalH = v == 0 ? 0 : (float)v / max * chartH;
                    var critH  = c2 == 0 ? 0 : (float)c2 / max * chartH;
                    var nonCritH = totalH - critH;

                    r.RelativeItem().PaddingHorizontal(0.6f).AlignBottom().Column(bc =>
                    {
                        if (critH > 0)  bc.Item().Height(critH).Background(SevCritical);
                        if (nonCritH > 0) bc.Item().Height(nonCritH).Background("#3B82F6");
                    });
                }
            });

            // Etykiety bucket'ów — pokazujemy datę pierwszego dnia w bucket'cie, co N-ty
            col.Item().PaddingTop(4).Row(r =>
            {
                var step = Math.Max(1, barCount / 8);
                for (int i = 0; i < barCount; i++)
                {
                    if (i % step == 0)
                    {
                        var d = fromDate.AddDays((long)i * bucketSize);
                        r.RelativeItem().Text(d.ToString("dd.MM")).FontSize(7).FontColor(InkMuted);
                    }
                    else
                    {
                        r.RelativeItem().Text("");
                    }
                }
            });

            // Legenda mini
            col.Item().PaddingTop(6).Row(r =>
            {
                r.Spacing(14);
                r.AutoItem().Row(rr =>
                {
                    rr.AutoItem().AlignMiddle().Width(8).Height(8).Background(SevCritical);
                    rr.AutoItem().PaddingLeft(4).AlignMiddle().Text("Krytyczne").FontSize(8).FontColor(InkSecondary);
                });
                r.AutoItem().Row(rr =>
                {
                    rr.AutoItem().AlignMiddle().Width(8).Height(8).Background("#3B82F6");
                    rr.AutoItem().PaddingLeft(4).AlignMiddle().Text("Pozostałe").FontSize(8).FontColor(InkSecondary);
                });
            });
        });
    }

    private static void CategoryBars(IContainer c, List<IGrouping<string, Incident>> byCategory, int total)
    {
        c.Column(col =>
        {
            col.Item().Text("Top kategorie zagrożeń").FontSize(13).Bold().FontColor(InkPrimary);
            col.Item().PaddingTop(2).Text("Najczęściej wykrywane kategorie naruszeń (do 8 czołowych).")
                .FontSize(9).FontColor(InkSecondary);

            if (byCategory.Count == 0)
            {
                col.Item().PaddingTop(8).Text("Brak danych w wybranym okresie.")
                    .FontSize(9).FontColor(InkMuted);
                return;
            }

            var max = byCategory.Max(g => g.Count());
            int idx = 0;
            foreach (var g in byCategory)
            {
                var color = CategoryPalette[idx % CategoryPalette.Length];
                var ratio = (float)g.Count() / max;
                var pctOfTotal = total == 0 ? 0 : g.Count() * 100.0 / total;

                col.Item().PaddingTop(idx == 0 ? 8 : 4).Row(row =>
                {
                    row.ConstantItem(160).AlignMiddle().Text(g.Key).FontSize(10).FontColor(InkPrimary);
                    row.RelativeItem().AlignMiddle().Column(barCol =>
                    {
                        barCol.Item().Background(SurfaceAlt).Height(14).Row(barRow =>
                        {
                            barRow.RelativeItem(ratio).Background(color);
                            if (ratio < 1) barRow.RelativeItem(1 - ratio);
                        });
                    });
                    row.ConstantItem(70).AlignMiddle().AlignRight().Text(t =>
                    {
                        t.Span($"{g.Count()}").FontSize(11).Bold().FontColor(InkPrimary);
                        t.Span($"  {pctOfTotal:F0}%").FontSize(9).FontColor(InkMuted);
                    });
                });
                idx++;
            }
        });
    }

    private static void CameraHotspots(IContainer c, List<IGrouping<string, Incident>> byCamera, int total)
    {
        c.Column(col =>
        {
            col.Item().Text("Punkty zapalne (kamery)").FontSize(13).Bold().FontColor(InkPrimary);
            col.Item().PaddingTop(2).Text("Stanowiska o największej liczbie zdarzeń — wymagają interwencji w pierwszej kolejności.")
                .FontSize(9).FontColor(InkSecondary);

            var max = byCamera.Max(g => g.Count());
            int idx = 0;
            foreach (var g in byCamera)
            {
                var ratio = (float)g.Count() / max;
                var pctOfTotal = total == 0 ? 0 : g.Count() * 100.0 / total;

                col.Item().PaddingTop(idx == 0 ? 8 : 4).Row(row =>
                {
                    row.ConstantItem(28).AlignMiddle().Background(SevCritical).Padding(4)
                        .Text($"#{idx + 1}").FontSize(10).Bold().FontColor("#FFFFFF").AlignCenter();
                    row.ConstantItem(160).PaddingLeft(8).AlignMiddle().Text(g.Key).FontSize(10).FontColor(InkPrimary);
                    row.RelativeItem().PaddingLeft(4).AlignMiddle().Column(bc =>
                    {
                        bc.Item().Background(SurfaceAlt).Height(12).Row(br =>
                        {
                            br.RelativeItem(ratio).Background(SevCritical);
                            if (ratio < 1) br.RelativeItem(1 - ratio);
                        });
                    });
                    row.ConstantItem(70).AlignMiddle().AlignRight().Text(t =>
                    {
                        t.Span($"{g.Count()}").FontSize(11).Bold().FontColor(InkPrimary);
                        t.Span($"  {pctOfTotal:F0}%").FontSize(9).FontColor(InkMuted);
                    });
                });
                idx++;
            }
        });
    }

    private static void IncidentsListHeader(IContainer c, int total, bool withThumbnails)
    {
        c.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("Lista incydentów").FontSize(13).Bold().FontColor(InkPrimary);
                row.AutoItem().AlignMiddle().Text(t =>
                {
                    t.Span("łącznie ").FontSize(9).FontColor(InkMuted);
                    t.Span($"{total}").FontSize(10).Bold().FontColor(InkPrimary);
                });
            });
            col.Item().PaddingTop(2).Text(withThumbnails
                    ? "Każda pozycja zawiera klatkę dowodową z momentu zdarzenia."
                    : "Pełna lista wykrytych zdarzeń uporządkowana chronologicznie.")
                .FontSize(9).FontColor(InkSecondary);
        });
    }

    private static void IncidentRow(IContainer c, Incident inc, byte[]? thumb, bool withThumbnails)
    {
        c.PaddingVertical(2).Row(row =>
        {
            // Lewy pasek severity — cienki, sygnał wizualny
            row.ConstantItem(2).Background(SeverityColor(inc.Severity));

            // Opcjonalna miniatura — większa (120×72, aspect ~5:3 dla CCTV) z subtelną ramką
            if (withThumbnails && IsValidImage(thumb))
            {
                row.ConstantItem(126).PaddingLeft(6).Height(72).Element(ic =>
                {
                    try { ic.Border(0.4f).BorderColor(Hairline).Image(thumb!).FitArea(); }
                    catch { ic.AlignCenter().AlignMiddle().Text("—").FontSize(7).FontColor(InkMuted); }
                });
            }

            row.RelativeItem().PaddingLeft(8).PaddingRight(4).Column(c2 =>
            {
                // Linia 1: data · kamera · [chip severity po prawej]
                c2.Item().Row(r =>
                {
                    r.AutoItem().AlignMiddle().Text(inc.OccurredAt.ToString("yyyy-MM-dd HH:mm"))
                        .FontSize(9).SemiBold().FontColor(InkPrimary);
                    r.AutoItem().PaddingLeft(6).AlignMiddle().Text(inc.CameraName)
                        .FontSize(9).FontColor(InkSecondary);
                    r.RelativeItem();
                    r.AutoItem().AlignMiddle().Background(SeverityColor(inc.Severity))
                        .PaddingHorizontal(5).PaddingVertical(0)
                        .Text(inc.Severity.ToString().ToUpperInvariant())
                        .FontSize(7).Bold().FontColor("#FFFFFF").LetterSpacing(0.3f);
                });
                // Linia 2: kategoria + summary inline (oszczędza pionowe miejsce)
                if (!string.IsNullOrEmpty(inc.Category) || !string.IsNullOrEmpty(inc.Summary))
                {
                    c2.Item().PaddingTop(1).Text(t =>
                    {
                        if (!string.IsNullOrEmpty(inc.Category))
                        {
                            t.Span(inc.Category).FontSize(8).FontColor(InkMuted);
                            if (!string.IsNullOrEmpty(inc.Summary))
                                t.Span("  ·  ").FontSize(8).FontColor(InkMuted);
                        }
                        if (!string.IsNullOrEmpty(inc.Summary))
                            t.Span(inc.Summary).FontSize(9).FontColor(InkPrimary);
                    });
                }
            });
        });
    }

    /// <summary>JPEG (FF D8 FF) lub PNG (89 50 4E 47) magic bytes. Inne formaty (webp, gif,
    /// uszkodzone) odrzucamy — QuestPDF rzuca exception przy renderze nieobsługiwanego obrazu.</summary>
    private static bool IsValidImage(byte[]? b)
    {
        if (b is null || b.Length < 8) return false;
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true; // JPEG
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true; // PNG
        return false;
    }

    // ── PDF kompaktowy (text + tabele, czarno-biały) ───────────────────────
    // Forma do druku / archiwum / audytu: zero kolorów decyzyjnych, maksymalna gęstość
    // informacji per strona, natywne page-breaking przez Table (każdy wiersz to osobny
    // page-break candidate, nie potrzeba zewnętrznego Column-per-Item).
    private const string CmpInk    = "#111111";
    private const string CmpMuted  = "#666666";
    private const string CmpRule   = "#D0D0D0";
    private const string CmpZebra  = "#F4F4F4";

    private static QuestPDF.Fluent.Document BuildPdfCompact(ReportFilter f, List<Incident> data)
    {
        var generatedAt = DateTime.Now;
        var bySeverity = data.GroupBy(i => i.Severity).ToDictionary(g => g.Key, g => g.Count());
        var byCategory = data.GroupBy(i => string.IsNullOrEmpty(i.Category) ? "(brak)" : i.Category)
                             .OrderByDescending(g => g.Count()).Take(10).ToList();
        var groupedByDay = data.GroupBy(i => i.OccurredAt.Date)
                               .OrderBy(g => g.Key).ToList();

        return QuestPDF.Fluent.Document.Create(doc =>
        {
            doc.Page(p =>
            {
                p.Size(PageSizes.A4);
                p.Margin(32);
                p.PageColor("#FFFFFF");
                p.DefaultTextStyle(t => t.FontSize(9).FontFamily(Fonts.Lato).FontColor(CmpInk));

                p.Header().PaddingBottom(6).Column(col =>
                {
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Text(t =>
                        {
                            t.Span("SAFEVIEW").FontSize(10).Bold().LetterSpacing(0.4f);
                            t.Span("  ·  Raport incydentów").FontSize(10).FontColor(CmpMuted);
                        });
                        r.AutoItem().Text(t =>
                        {
                            t.Span($"{f.From:yyyy-MM-dd HH:mm}").FontSize(9).Bold();
                            t.Span(" → ").FontSize(9).FontColor(CmpMuted);
                            t.Span($"{f.To:yyyy-MM-dd HH:mm}").FontSize(9).Bold();
                            t.Span($"   ·   wygenerowano {generatedAt:yyyy-MM-dd HH:mm}").FontSize(8).FontColor(CmpMuted);
                        });
                    });
                    col.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(CmpInk);
                });

                p.Content().PaddingTop(10).Column(col =>
                {
                    col.Spacing(14);

                    // ── Podsumowanie ważności (1-linijkowy panel)
                    col.Item().Element(SectionTitle("Podsumowanie"));
                    col.Item().Border(0.5f).BorderColor(CmpRule).Padding(8).Row(r =>
                    {
                        CmpKpi(r, "Łącznie",   data.Count);
                        CmpKpi(r, "Krytyczne", bySeverity.GetValueOrDefault(IncidentSeverity.Critical));
                        CmpKpi(r, "Wysokie",   bySeverity.GetValueOrDefault(IncidentSeverity.High));
                        CmpKpi(r, "Średnie",   bySeverity.GetValueOrDefault(IncidentSeverity.Medium));
                        CmpKpi(r, "Niskie",    bySeverity.GetValueOrDefault(IncidentSeverity.Low));
                    });

                    // ── Top kategorie
                    col.Item().Element(SectionTitle($"Top kategorie  ({byCategory.Count})"));
                    if (byCategory.Count == 0)
                        col.Item().Text("Brak danych w wybranym okresie.").FontSize(9).FontColor(CmpMuted);
                    else
                    {
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.RelativeColumn(5); c.ConstantColumn(50); c.ConstantColumn(50); });
                            t.Header(h =>
                            {
                                h.Cell().Element(CmpHeadCell).Text("Kategoria");
                                h.Cell().Element(CmpHeadCell).AlignRight().Text("Liczba");
                                h.Cell().Element(CmpHeadCell).AlignRight().Text("Udział");
                            });
                            int row = 0;
                            foreach (var g in byCategory)
                            {
                                var pct = data.Count == 0 ? 0 : g.Count() * 100.0 / data.Count;
                                Func<IContainer, IContainer> rowEl = (row++ % 2 == 0) ? CmpBodyCell : CmpBodyCellZebra;
                                t.Cell().Element(rowEl).Text(g.Key);
                                t.Cell().Element(rowEl).AlignRight().Text(g.Count().ToString());
                                t.Cell().Element(rowEl).AlignRight().Text($"{pct:F0}%");
                            }
                        });
                    }

                    // ── Rozkład dzienny
                    col.Item().Element(SectionTitle($"Rozkład dzienny  ({groupedByDay.Count} dni)"));
                    if (groupedByDay.Count == 0)
                        col.Item().Text("Brak danych w wybranym okresie.").FontSize(9).FontColor(CmpMuted);
                    else
                    {
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(2); c.ConstantColumn(50);
                                c.ConstantColumn(60); c.ConstantColumn(50); c.ConstantColumn(50); c.ConstantColumn(50);
                            });
                            t.Header(h =>
                            {
                                h.Cell().Element(CmpHeadCell).Text("Data");
                                h.Cell().Element(CmpHeadCell).AlignRight().Text("Łącznie");
                                h.Cell().Element(CmpHeadCell).AlignRight().Text("Krytyczne");
                                h.Cell().Element(CmpHeadCell).AlignRight().Text("Wysokie");
                                h.Cell().Element(CmpHeadCell).AlignRight().Text("Średnie");
                                h.Cell().Element(CmpHeadCell).AlignRight().Text("Niskie");
                            });
                            int row = 0;
                            foreach (var g in groupedByDay)
                            {
                                Func<IContainer, IContainer> rowEl = (row++ % 2 == 0) ? CmpBodyCell : CmpBodyCellZebra;
                                t.Cell().Element(rowEl).Text(g.Key.ToString("yyyy-MM-dd ddd"));
                                t.Cell().Element(rowEl).AlignRight().Text(g.Count().ToString());
                                t.Cell().Element(rowEl).AlignRight().Text(g.Count(i => i.Severity == IncidentSeverity.Critical).ToString());
                                t.Cell().Element(rowEl).AlignRight().Text(g.Count(i => i.Severity == IncidentSeverity.High).ToString());
                                t.Cell().Element(rowEl).AlignRight().Text(g.Count(i => i.Severity == IncidentSeverity.Medium).ToString());
                                t.Cell().Element(rowEl).AlignRight().Text(g.Count(i => i.Severity == IncidentSeverity.Low).ToString());
                            }
                        });
                    }

                    // ── Pełna lista incydentów (Table = natywny page-break per row)
                    col.Item().Element(SectionTitle($"Lista incydentów  ({data.Count})"));
                    if (data.Count == 0)
                        col.Item().Text("Brak incydentów w wybranym okresie.").FontSize(9).FontColor(CmpMuted);
                    else
                    {
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(95);  // datetime
                                c.RelativeColumn(2);   // camera
                                c.RelativeColumn(2);   // category
                                c.ConstantColumn(45);  // severity
                                c.RelativeColumn(4);   // summary
                            });
                            t.Header(h =>
                            {
                                h.Cell().Element(CmpHeadCell).Text("Kiedy");
                                h.Cell().Element(CmpHeadCell).Text("Kamera");
                                h.Cell().Element(CmpHeadCell).Text("Kategoria");
                                h.Cell().Element(CmpHeadCell).Text("Ważność");
                                h.Cell().Element(CmpHeadCell).Text("Podsumowanie");
                            });
                            int row = 0;
                            foreach (var inc in data.OrderByDescending(i => i.OccurredAt))
                            {
                                Func<IContainer, IContainer> rowEl = (row++ % 2 == 0) ? CmpBodyCell : CmpBodyCellZebra;
                                t.Cell().Element(rowEl).Text(inc.OccurredAt.ToString("yyyy-MM-dd HH:mm"));
                                t.Cell().Element(rowEl).Text(inc.CameraName);
                                t.Cell().Element(rowEl).Text(inc.Category);
                                t.Cell().Element(rowEl).Text(inc.Severity.ToString().ToUpperInvariant()).FontSize(8).Bold();
                                t.Cell().Element(rowEl).Text(inc.Summary);
                            }
                        });
                    }
                });

                p.Footer().PaddingTop(6).BorderTop(0.5f).BorderColor(CmpRule).PaddingTop(4).Row(r =>
                {
                    r.RelativeItem().Text("SafeView · poufne — do użytku wewnętrznego")
                        .FontSize(8).FontColor(CmpMuted);
                    r.AutoItem().Text(x =>
                    {
                        x.Span("strona ").FontSize(8).FontColor(CmpMuted);
                        x.CurrentPageNumber().FontSize(8).Bold();
                        x.Span(" / ").FontSize(8).FontColor(CmpMuted);
                        x.TotalPages().FontSize(8).Bold();
                    });
                });
            });
        });
    }

    private static Action<IContainer> SectionTitle(string title) => container =>
    {
        container.Column(col =>
        {
            col.Item().Text(title).FontSize(11).Bold().LetterSpacing(0.3f);
            col.Item().PaddingTop(3).LineHorizontal(0.5f).LineColor(CmpRule);
        });
    };

    private static void CmpKpi(RowDescriptor r, string label, int value)
    {
        r.RelativeItem().Column(col =>
        {
            col.Item().Text(label).FontSize(8).FontColor(CmpMuted).LetterSpacing(0.3f);
            col.Item().PaddingTop(2).Text(value.ToString()).FontSize(16).Bold().FontColor(CmpInk);
        });
    }

    private static IContainer CmpHeadCell(IContainer c) => c
        .BorderBottom(0.6f).BorderColor(CmpInk).PaddingVertical(4).PaddingHorizontal(5)
        .DefaultTextStyle(t => t.SemiBold().FontSize(8).LetterSpacing(0.2f));

    private static IContainer CmpBodyCell(IContainer c) => c
        .BorderBottom(0.25f).BorderColor(CmpRule).PaddingVertical(3).PaddingHorizontal(5)
        .DefaultTextStyle(t => t.FontSize(8.5f));

    private static IContainer CmpBodyCellZebra(IContainer c) => c
        .Background(CmpZebra).BorderBottom(0.25f).BorderColor(CmpRule).PaddingVertical(3).PaddingHorizontal(5)
        .DefaultTextStyle(t => t.FontSize(8.5f));
}
