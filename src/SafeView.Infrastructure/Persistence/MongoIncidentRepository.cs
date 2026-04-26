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
