using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Incidents;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoIncidentRepository : MongoRepositoryBase<Incident>, IIncidentRepository
{
    public const string CollectionName = "incidents";

    public MongoIncidentRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateMany([
            new CreateIndexModel<Incident>(Builders<Incident>.IndexKeys.Descending(i => i.OccurredAt)),
            new CreateIndexModel<Incident>(Builders<Incident>.IndexKeys.Ascending(i => i.CameraId).Descending(i => i.OccurredAt)),
            new CreateIndexModel<Incident>(Builders<Incident>.IndexKeys.Ascending(i => i.Status).Descending(i => i.OccurredAt)),
            new CreateIndexModel<Incident>(Builders<Incident>.IndexKeys.Ascending(i => i.VllmTemplateId).Descending(i => i.OccurredAt)),
            new CreateIndexModel<Incident>(Builders<Incident>.IndexKeys.Ascending(i => i.DetectionClassId).Descending(i => i.OccurredAt))
        ]);
    }

    public async Task<IReadOnlyList<Incident>> ListRecentAsync(int limit = 100, CancellationToken ct = default)
        => await Col.Find(FilterDefinition<Incident>.Empty)
            .SortByDescending(i => i.OccurredAt)
            .Limit(limit)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<Incident>> ListByCameraAsync(string cameraId, int limit = 100, CancellationToken ct = default)
        => await Col.Find(i => i.CameraId == cameraId)
            .SortByDescending(i => i.OccurredAt)
            .Limit(limit)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<Incident>> ListByStatusAsync(IncidentStatus status, int limit = 100, CancellationToken ct = default)
        => await Col.Find(i => i.Status == status)
            .SortByDescending(i => i.OccurredAt)
            .Limit(limit)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<Incident>> ListByCameraAndDateRangeAsync(string cameraId, DateTime from, DateTime until, CancellationToken ct = default)
        => await Col.Find(i => i.CameraId == cameraId && i.OccurredAt >= from && i.OccurredAt < until)
            .SortByDescending(i => i.OccurredAt)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<Incident>> ListByDateRangeAsync(DateTime from, DateTime until, CancellationToken ct = default)
        => await Col.Find(i => i.OccurredAt >= from && i.OccurredAt < until)
            .SortBy(i => i.OccurredAt)
            .ToListAsync(ct).ConfigureAwait(false);

    public Task<long> CountAsync(DateTime? from = null, DateTime? until = null,
        IncidentSeverity? severity = null, IncidentStatus? status = null,
        CancellationToken ct = default)
    {
        var b = Builders<Incident>.Filter;
        var filter = FilterDefinition<Incident>.Empty;
        if (from  is not null) filter &= b.Gte(i => i.OccurredAt, from.Value);
        if (until is not null) filter &= b.Lt(i => i.OccurredAt, until.Value);
        if (severity is not null) filter &= b.Eq(i => i.Severity, severity.Value);
        if (status   is not null) filter &= b.Eq(i => i.Status, status.Value);
        return Col.CountDocumentsAsync(filter, cancellationToken: ct);
    }

    public async Task<PagedResult<Incident>> ListPagedAsync(IncidentFilter filter, int skip, int limit, CancellationToken ct = default)
    {
        var mongoFilter = BuildFilter(filter);
        // Total + page in parallel — 1 round-trip-batch zamiast 2 sekwencyjnych.
        // Total potrzebny do pagera "245 of 1234"; bez niego MudTablePager nie wie ile stron renderować.
        var totalTask = Col.CountDocumentsAsync(mongoFilter, cancellationToken: ct);
        var pageTask = Col.Find(mongoFilter)
            .SortByDescending(i => i.OccurredAt)
            .Skip(skip)
            .Limit(limit)
            .ToListAsync(ct);
        await Task.WhenAll(totalTask, pageTask).ConfigureAwait(false);
        return new PagedResult<Incident>(pageTask.Result, totalTask.Result);
    }

    public async Task<IReadOnlyDictionary<IncidentSeverity, long>> CountBySeverityAsync(IncidentFilter filter, CancellationToken ct = default)
    {
        var baseFilter = BuildFilter(filter, ignoreSeverity: true);
        var b = Builders<Incident>.Filter;

        // 4 paralelne CountDocumentsAsync — każdy idzie po indeksie (OccurredAt) + filter
        // na Severity. Szybsze niż ToList+GroupBy bo nie wczytuje payloadu dokumentów.
        // CountBySeverity ignoruje filter.MinSeverity — zwraca pełny histogram, UI sam decyduje
        // co pokazać (np. KPI dla wszystkich severity, nie tylko tych nad progiem).
        var critTask = Col.CountDocumentsAsync(baseFilter & b.Eq(i => i.Severity, IncidentSeverity.Critical), cancellationToken: ct);
        var highTask = Col.CountDocumentsAsync(baseFilter & b.Eq(i => i.Severity, IncidentSeverity.High), cancellationToken: ct);
        var medTask = Col.CountDocumentsAsync(baseFilter & b.Eq(i => i.Severity, IncidentSeverity.Medium), cancellationToken: ct);
        var lowTask = Col.CountDocumentsAsync(baseFilter & b.Eq(i => i.Severity, IncidentSeverity.Low), cancellationToken: ct);
        await Task.WhenAll(critTask, highTask, medTask, lowTask).ConfigureAwait(false);
        return new Dictionary<IncidentSeverity, long>
        {
            [IncidentSeverity.Critical] = critTask.Result,
            [IncidentSeverity.High] = highTask.Result,
            [IncidentSeverity.Medium] = medTask.Result,
            [IncidentSeverity.Low] = lowTask.Result,
        };
    }

    /// <summary>
    /// Buduje FilterDefinition z <see cref="IncidentFilter"/>. Dla <c>FalsePositiveAny</c>
    /// emituje <c>$or</c> (Status=FP ∨ WasFalsePositive=true) — operator może oznaczyć FP
    /// flagą bez zmiany statusu.
    /// </summary>
    /// <param name="ignoreSeverity">Gdy true, pomija filtr MinSeverity — używane przez
    /// <see cref="CountBySeverityAsync"/> żeby zwrócić pełny histogram.</param>
    private static FilterDefinition<Incident> BuildFilter(IncidentFilter filter, bool ignoreSeverity = false)
    {
        var b = Builders<Incident>.Filter;
        var def = FilterDefinition<Incident>.Empty;
        if (filter.From is not null) def &= b.Gte(i => i.OccurredAt, filter.From.Value);
        if (filter.Until is not null) def &= b.Lt(i => i.OccurredAt, filter.Until.Value);
        if (!string.IsNullOrEmpty(filter.CameraId)) def &= b.Eq(i => i.CameraId, filter.CameraId);
        if (!ignoreSeverity && filter.MinSeverity is not null)
            def &= b.Gte(i => i.Severity, filter.MinSeverity.Value);

        def &= filter.Status switch
        {
            IncidentStatusOption.Open => b.Eq(i => i.Status, IncidentStatus.Open),
            IncidentStatusOption.Acknowledged => b.Eq(i => i.Status, IncidentStatus.Acknowledged),
            IncidentStatusOption.Resolved => b.Eq(i => i.Status, IncidentStatus.Resolved),
            IncidentStatusOption.FalsePositiveAny =>
                b.Or(b.Eq(i => i.Status, IncidentStatus.FalsePositive), b.Eq(i => i.WasFalsePositive, true)),
            _ => FilterDefinition<Incident>.Empty
        };
        return def;
    }

    public async Task<VllmTemplateStats> GetStatsByVllmTemplateAsync(string templateId, DateTime from, CancellationToken ct = default)
    {
        var docs = await Col
            .Find(i => i.VllmTemplateId == templateId && i.OccurredAt >= from)
            .Project(i => new { i.OccurredAt, i.WasFalsePositive, i.VllmConfidence })
            .ToListAsync(ct).ConfigureAwait(false);

        var empty = new int[10];
        if (docs.Count == 0) return new VllmTemplateStats(0, 0, null, null, empty);

        long total = docs.Count;
        long fp = docs.Count(d => d.WasFalsePositive);
        var confidences = docs.Where(d => d.VllmConfidence.HasValue).Select(d => d.VllmConfidence!.Value).ToList();
        double? avgConf = confidences.Count > 0 ? confidences.Average() : null;
        var lastFire = docs.Max(d => d.OccurredAt);

        // Histogram: 10 bucketów po 0.1, wartość 1.0 wpada do ostatniego
        var hist = new int[10];
        foreach (var c in confidences)
        {
            var idx = (int)Math.Floor(Math.Clamp(c, 0, 0.9999) * 10);
            hist[idx]++;
        }

        return new VllmTemplateStats(total, fp, avgConf, lastFire, hist);
    }

    public async Task<DetectionClassStats> GetStatsByDetectionClassAsync(string classId, DateTime from, CancellationToken ct = default)
    {
        var docs = await Col
            .Find(i => i.DetectionClassId == classId && i.OccurredAt >= from)
            .Project(i => new { i.OccurredAt, i.WasFalsePositive, i.DetectionConfidence })
            .ToListAsync(ct).ConfigureAwait(false);

        var empty = new int[10];
        if (docs.Count == 0) return new DetectionClassStats(0, 0, null, null, empty);

        long total = docs.Count;
        long fp = docs.Count(d => d.WasFalsePositive);
        var confidences = docs.Where(d => d.DetectionConfidence.HasValue)
            .Select(d => d.DetectionConfidence!.Value).ToList();
        double? avgConf = confidences.Count > 0 ? confidences.Average() : null;
        var lastFire = docs.Max(d => d.OccurredAt);

        var hist = new int[10];
        foreach (var c in confidences)
        {
            var idx = (int)Math.Floor(Math.Clamp(c, 0, 0.9999) * 10);
            hist[idx]++;
        }

        return new DetectionClassStats(total, fp, avgConf, lastFire, hist);
    }
}
