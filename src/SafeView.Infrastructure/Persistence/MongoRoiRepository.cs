using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Detection;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoRoiRepository : MongoRepositoryBase<Roi>, IRoiRepository
{
    public const string CollectionName = "rois";

    public MongoRoiRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateMany([
            new CreateIndexModel<Roi>(Builders<Roi>.IndexKeys.Ascending(r => r.CameraId)),
            new CreateIndexModel<Roi>(Builders<Roi>.IndexKeys.Ascending(r => r.Enabled)),
        ]);
    }

    public async Task<IReadOnlyList<Roi>> ListByCameraAsync(string cameraId, CancellationToken ct = default)
        => await Col.Find(r => r.CameraId == cameraId).ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<Roi>> ListEnabledByCameraAsync(string cameraId, CancellationToken ct = default)
        => await Col.Find(r => r.CameraId == cameraId && r.Enabled).ToListAsync(ct).ConfigureAwait(false);
}
