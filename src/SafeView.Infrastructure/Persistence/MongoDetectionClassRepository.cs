using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Detection;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoDetectionClassRepository : MongoRepositoryBase<DetectionClass>, IDetectionClassRepository
{
    public const string CollectionName = "detection_classes";

    public MongoDetectionClassRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateMany([
            new CreateIndexModel<DetectionClass>(Builders<DetectionClass>.IndexKeys.Ascending(c => c.Category)),
            new CreateIndexModel<DetectionClass>(Builders<DetectionClass>.IndexKeys.Ascending(c => c.BuiltInKey)),
            new CreateIndexModel<DetectionClass>(Builders<DetectionClass>.IndexKeys.Ascending(c => c.Name)),
            new CreateIndexModel<DetectionClass>(Builders<DetectionClass>.IndexKeys
                .Ascending(c => c.ClosedSetModelId).Ascending(c => c.ClosedSetLabel)),
        ]);
    }

    public async Task<IReadOnlyList<DetectionClass>> ListByCategoryAsync(string category, CancellationToken ct = default)
        => await Col.Find(c => c.Category == category)
            .SortBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<DetectionClass?> GetByBuiltInKeyAsync(string key, CancellationToken ct = default)
        => await Col.Find(c => c.BuiltInKey == key).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<DetectionClass?> FindByClosedSetBindingAsync(
        string modelId, string label, CancellationToken ct = default)
        => await Col.Find(c =>
                c.Kind == DetectionClassKind.ClosedSetBinding
                && c.ClosedSetModelId == modelId
                && c.ClosedSetLabel == label)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<DetectionClass>> ListByIdsAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        return await Col.Find(Builders<DetectionClass>.Filter.In(c => c.Id, ids))
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
