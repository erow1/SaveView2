using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SafeView.Application.Abstractions.Diagnostics;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Configuration;
using SafeView.Domain.SystemEvents;

namespace SafeView.Infrastructure.Persistence;

/// <summary>
/// MongoDB-owy backend dla <see cref="ISystemEventLogger"/> i <see cref="ISystemEventQuery"/>.
/// Kolekcja <c>system_events</c> ma TTL index na <c>CreatedAt</c> — MongoDB sam usuwa
/// rekordy starsze niż <see cref="DiagnosticsOptions.RetentionDays"/>.
///
/// UWAGA: ten logger <b>nie może mieć zależności od <c>ILogger&lt;T&gt;</c></b>.
/// Jest resolvowany z Serilog sink factory podczas budowy hosta — circular z LoggerFactory
/// (który buduje Serilog) spowodowałby deadlock. Błędy idą przez Console.Error.
/// </summary>
public sealed class MongoSystemEventLogger : ISystemEventLogger, ISystemEventQuery
{
    public const string CollectionName = "system_events";

    private readonly IMongoCollection<SystemEvent> _col;

    public MongoSystemEventLogger(
        IMongoContext ctx,
        IOptions<DiagnosticsOptions> diagOpts)
    {
        _col = ctx.Collection<SystemEvent>(CollectionName);

        // WAŻNE: ten logger jest resolvovany podczas startu hosta (Serilog sink factory),
        // więc konstruktor MUSI zwrócić natychmiast. CreateMany jest synchroniczne
        // i na brak/wolnym Mongo czeka 30s server selection timeout — blokowałoby cały start.
        // Dlatego zakładanie indeksów leci w tle (fire-and-forget).
        var retention = TimeSpan.FromDays(Math.Max(1, diagOpts.Value.RetentionDays));
        _ = Task.Run(() => EnsureIndexesAsync(retention));
    }

    private async Task EnsureIndexesAsync(TimeSpan retention)
    {
        try
        {
            await _col.Indexes.CreateManyAsync([
                new CreateIndexModel<SystemEvent>(
                    Builders<SystemEvent>.IndexKeys.Ascending(e => e.CreatedAt),
                    new CreateIndexOptions { Name = "ttl_created_at", ExpireAfter = retention }),
                new CreateIndexModel<SystemEvent>(
                    Builders<SystemEvent>.IndexKeys.Descending(e => e.CreatedAt),
                    new CreateIndexOptions { Name = "created_at_desc" }),
                new CreateIndexModel<SystemEvent>(
                    Builders<SystemEvent>.IndexKeys.Ascending(e => e.Severity),
                    new CreateIndexOptions { Name = "severity" }),
                new CreateIndexModel<SystemEvent>(
                    Builders<SystemEvent>.IndexKeys.Ascending(e => e.Source),
                    new CreateIndexOptions { Name = "source" }),
            ]).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (ex.CodeName == "IndexOptionsConflict")
        {
            // TTL retention zmieniony — drop + re-create samego TTL indeksu
            try
            {
                await _col.Indexes.DropOneAsync("ttl_created_at").ConfigureAwait(false);
                await _col.Indexes.CreateOneAsync(new CreateIndexModel<SystemEvent>(
                    Builders<SystemEvent>.IndexKeys.Ascending(e => e.CreatedAt),
                    new CreateIndexOptions { Name = "ttl_created_at", ExpireAfter = retention })).ConfigureAwait(false);
            }
            catch (Exception inner)
            {
                // Nie loguj przez _log — potencjalna pętla przez Serilog sink.
                Console.Error.WriteLine($"[MongoSystemEventLogger] Failed to re-create TTL index: {inner.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MongoSystemEventLogger] Failed to create indexes on {CollectionName}: {ex.Message}");
        }
    }

    public async Task LogAsync(SystemEvent evt, CancellationToken ct = default)
    {
        try
        {
            evt.UpdatedAt = DateTime.UtcNow;
            await _col.InsertOneAsync(evt, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nie loguj przez _log (infinite loop przez Serilog sink!) — tylko stderr.
            Console.Error.WriteLine($"[SystemEventLogger] Insert failed: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventFilter filter, int skip, int take, CancellationToken ct = default)
    {
        var f = BuildFilter(filter);
        return await _col.Find(f)
            .SortByDescending(e => e.CreatedAt)
            .Skip(skip).Limit(take)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<long> CountAsync(SystemEventFilter filter, CancellationToken ct = default)
        => _col.CountDocumentsAsync(BuildFilter(filter), cancellationToken: ct);

    public async Task<long> PurgeOlderThanAsync(TimeSpan retention, CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow - retention;
        var res = await _col.DeleteManyAsync(e => e.CreatedAt < threshold, ct).ConfigureAwait(false);
        return res.DeletedCount;
    }

    private static FilterDefinition<SystemEvent> BuildFilter(SystemEventFilter filter)
    {
        var fb = Builders<SystemEvent>.Filter;
        var f = fb.Empty;

        if (filter.MinSeverity is { } min)
            f &= fb.Gte(e => e.Severity, min);
        if (!string.IsNullOrWhiteSpace(filter.Source))
            f &= fb.Eq(e => e.Source, filter.Source);
        if (!string.IsNullOrWhiteSpace(filter.Category))
            f &= fb.Eq(e => e.Category, filter.Category);
        if (filter.Since is { } since)
            f &= fb.Gte(e => e.CreatedAt, since);
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var regex = new BsonRegularExpression(filter.SearchText, "i");
            f &= fb.Or(
                fb.Regex(e => e.Message, regex),
                fb.Regex(e => e.ExceptionMessage, regex)
            );
        }

        return f;
    }
}
