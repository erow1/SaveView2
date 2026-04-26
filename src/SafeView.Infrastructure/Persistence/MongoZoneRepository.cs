using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Zones;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoZoneRepository : MongoRepositoryBase<Zone>, IZoneRepository
{
    public const string CollectionName = "zones";

    public MongoZoneRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateOne(new CreateIndexModel<Zone>(
            Builders<Zone>.IndexKeys.Ascending(z => z.CameraId).Ascending(z => z.Name)));
        Col.Indexes.CreateOne(new CreateIndexModel<Zone>(
            Builders<Zone>.IndexKeys.Ascending(z => z.Enabled)));
    }

    public async Task<IReadOnlyList<Zone>> ListByCameraAsync(string cameraId, CancellationToken ct = default)
        => await Col.Find(z => z.CameraId == cameraId).ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<Zone>> ListEnabledAsync(CancellationToken ct = default)
        => await Col.Find(z => z.Enabled).ToListAsync(ct).ConfigureAwait(false);
}
