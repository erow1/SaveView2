using SafeView.Domain.Incidents;

namespace SafeView.Application.Abstractions.Persistence;

public interface IIncidentRepository : IRepository<Incident>
{
    Task<IReadOnlyList<Incident>> ListRecentAsync(int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<Incident>> ListByCameraAsync(string cameraId, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<Incident>> ListByStatusAsync(IncidentStatus status, int limit = 100, CancellationToken ct = default);

    /// <summary>Wszystkie incydenty w przedziale [from, until) — sortowane rosnąco po OccurredAt.</summary>
    Task<IReadOnlyList<Incident>> ListByDateRangeAsync(DateTime from, DateTime until, CancellationToken ct = default);

    /// <summary>Incydenty dla konkretnej kamery w przedziale [from, until). Sort: malejąco po OccurredAt.</summary>
    Task<IReadOnlyList<Incident>> ListByCameraAndDateRangeAsync(string cameraId, DateTime from, DateTime until, CancellationToken ct = default);

    /// <summary>Liczba incydentów spełniających proste filtry. Wszystkie parametry opcjonalne.</summary>
    Task<long> CountAsync(DateTime? from = null, DateTime? until = null,
        IncidentSeverity? severity = null, IncidentStatus? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Strona wyników z TotalCount — server-side pagination dla /incidents. Zwraca
    /// pojedynczą stronę (Skip/Limit) + całkowitą liczbę dokumentów spełniających
    /// filtr (potrzebne do pagera + KPI). Sortowanie zawsze DESC po OccurredAt
    /// (zgodne z indeksem). Filtr "FalsePositiveAny" jest specjalny — dopasowuje
    /// Status=FalsePositive LUB WasFalsePositive=true (operator może oznaczyć FP
    /// nie zmieniając statusu).
    /// </summary>
    Task<PagedResult<Incident>> ListPagedAsync(IncidentFilter filter, int skip, int limit, CancellationToken ct = default);

    /// <summary>
    /// Histogram po Severity dla całego zbioru spełniającego filtr (NIE tylko aktualnej strony).
    /// Używane do KPI (Critical/High/Medium counts). 4 paralelne CountDocumentsAsync — szybsze niż
    /// ToList+GroupBy bo każdy CountDocuments idzie po indeksie i nie wczytuje payloadu.
    /// </summary>
    Task<IReadOnlyDictionary<IncidentSeverity, long>> CountBySeverityAsync(IncidentFilter filter, CancellationToken ct = default);

    /// <summary>Statystyki jakości szablonu VLLM w oknie czasowym. Używane w /admin/vllm-templates.</summary>
    Task<VllmTemplateStats> GetStatsByVllmTemplateAsync(string templateId, DateTime from, CancellationToken ct = default);

    /// <summary>Statystyki jakości <c>DetectionClass</c> w oknie czasowym (Faza 7).
    /// Analog do <see cref="GetStatsByVllmTemplateAsync"/>, ale agreguje po
    /// <c>Incident.DetectionClassId</c> + <c>DetectionConfidence</c>.</summary>
    Task<DetectionClassStats> GetStatsByDetectionClassAsync(string classId, DateTime from, CancellationToken ct = default);
}

/// <summary>
/// Zagregowane statystyki jakości szablonu VLLM.
/// <see cref="ConfidenceHistogram"/> to 10 bucketów (0.0-0.1, 0.1-0.2, …, 0.9-1.0);
/// element [i] to liczba incydentów z confidence w tym przedziale.
/// </summary>
public sealed record VllmTemplateStats(
    long FireCount,
    long FalsePositiveCount,
    double? AvgConfidence,
    DateTime? LastFireAt,
    int[] ConfidenceHistogram)
{
    public double FalsePositiveRate => FireCount > 0 ? (double)FalsePositiveCount / FireCount : 0;
}

/// <summary>
/// Zagregowane statystyki jakości <c>DetectionClass</c>. Ten sam layout histogramu co
/// <see cref="VllmTemplateStats"/> — 10 bucketów po 0.1, wartość 1.0 w ostatnim.
/// <see cref="AvgConfidence"/> uśrednia <c>Incident.DetectionConfidence</c> (raw model output),
/// nie <c>VllmConfidence</c>.
/// </summary>
public sealed record DetectionClassStats(
    long FireCount,
    long FalsePositiveCount,
    double? AvgConfidence,
    DateTime? LastFireAt,
    int[] ConfidenceHistogram)
{
    public double FalsePositiveRate => FireCount > 0 ? (double)FalsePositiveCount / FireCount : 0;
}

/// <summary>
/// Filtr dla <see cref="IIncidentRepository.ListPagedAsync"/> i <see cref="IIncidentRepository.CountBySeverityAsync"/>.
/// Wszystkie pola opcjonalne — null = brak filtra. <see cref="MinSeverity"/> jest progiem (>=),
/// nie equality — Critical+High+Medium zwracane gdy MinSeverity=Medium.
/// <see cref="StatusOption"/> "FalsePositiveAny" obejmuje oba sygnały FP (Status + WasFalsePositive flag).
/// </summary>
public sealed record IncidentFilter(
    DateTime? From = null,
    DateTime? Until = null,
    string? CameraId = null,
    IncidentSeverity? MinSeverity = null,
    IncidentStatusOption Status = IncidentStatusOption.All);

/// <summary>
/// UI-friendly status filter dla list incydentów. "FalsePositiveAny" to OR
/// (Status=FalsePositive ∨ WasFalsePositive=true) — operator może oznaczyć incydent
/// jako FP nie zmieniając jego statusu (np. Resolved + ThumbDown).
/// </summary>
public enum IncidentStatusOption
{
    All,
    Open,
    Acknowledged,
    Resolved,
    FalsePositiveAny
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, long TotalCount);
