using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Audit;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoAuditLogger : IAuditLogger, IAuditQuery
{
    public const string CollectionName = "audit_log";

    private readonly IMongoCollection<AuditEntry> _col;
    private readonly ILogger<MongoAuditLogger> _log;

    public MongoAuditLogger(IMongoContext ctx, ILogger<MongoAuditLogger> log)
    {
        _col = ctx.Collection<AuditEntry>(CollectionName);
        _log = log;

        _col.Indexes.CreateMany([
            new CreateIndexModel<AuditEntry>(Builders<AuditEntry>.IndexKeys.Descending(e => e.CreatedAt)),
            new CreateIndexModel<AuditEntry>(Builders<AuditEntry>.IndexKeys.Ascending(e => e.Username)),
            new CreateIndexModel<AuditEntry>(Builders<AuditEntry>.IndexKeys.Ascending(e => e.Action))
        ]);
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken ct = default)
    {
        try
        {
            entry.UpdatedAt = DateTime.UtcNow;
            await _col.InsertOneAsync(entry, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // audit must never break the business op
            _log.LogError(ex, "Failed to write audit entry for action {Action}", entry.Action);
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(int skip = 0, int take = 100, string? userFilter = null, string? actionFilter = null, CancellationToken ct = default)
    {
        var fb = Builders<AuditEntry>.Filter;
        var filter = fb.Empty;
        if (!string.IsNullOrWhiteSpace(userFilter))
            filter &= fb.Eq(e => e.Username, userFilter);
        if (!string.IsNullOrWhiteSpace(actionFilter))
            filter &= fb.Regex(e => e.Action, new MongoDB.Bson.BsonRegularExpression(actionFilter, "i"));

        return await _col.Find(filter)
            .SortByDescending(e => e.CreatedAt)
            .Skip(skip).Limit(take)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<long> CountAsync(string? userFilter = null, string? actionFilter = null, CancellationToken ct = default)
    {
        var fb = Builders<AuditEntry>.Filter;
        var filter = fb.Empty;
        if (!string.IsNullOrWhiteSpace(userFilter))
            filter &= fb.Eq(e => e.Username, userFilter);
        if (!string.IsNullOrWhiteSpace(actionFilter))
            filter &= fb.Regex(e => e.Action, new MongoDB.Bson.BsonRegularExpression(actionFilter, "i"));
        return _col.CountDocumentsAsync(filter, cancellationToken: ct);
    }
}
