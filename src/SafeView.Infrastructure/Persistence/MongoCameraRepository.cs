using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Cameras;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoCameraRepository : MongoRepositoryBase<Camera>, ICameraRepository
{
    public const string CollectionName = "cameras";

    public MongoCameraRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateOne(new CreateIndexModel<Camera>(
            Builders<Camera>.IndexKeys.Ascending(c => c.Name),
            new CreateIndexOptions { Unique = true }));
    }

    public Task<Camera?> FindByNameAsync(string name, CancellationToken ct = default)
        => Col.Find(c => c.Name == name).FirstOrDefaultAsync(ct)!;

    public async Task<IReadOnlyList<Camera>> ListEnabledAsync(CancellationToken ct = default)
        => await Col.Find(c => c.Enabled).ToListAsync(ct).ConfigureAwait(false);
}
