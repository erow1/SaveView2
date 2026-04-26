using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Detection;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoTriggerRepository : MongoRepositoryBase<Trigger>, ITriggerRepository
{
    public const string CollectionName = "triggers";

    public MongoTriggerRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateOne(new CreateIndexModel<Trigger>(
            Builders<Trigger>.IndexKeys.Ascending(t => t.Enabled)));
    }

    public async Task<IReadOnlyList<Trigger>> ListByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        return await Col.Find(t => ids.Contains(t.Id)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Trigger>> ListEnabledAsync(CancellationToken ct = default)
        => await Col.Find(t => t.Enabled).ToListAsync(ct).ConfigureAwait(false);
}
