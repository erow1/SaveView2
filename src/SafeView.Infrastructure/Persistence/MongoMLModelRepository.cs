using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.ML;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoMLModelRepository : MongoRepositoryBase<MLModel>, IMLModelRepository
{
    public const string CollectionName = "ml_models";

    public MongoMLModelRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateOne(new CreateIndexModel<MLModel>(
            Builders<MLModel>.IndexKeys.Ascending(m => m.Name),
            new CreateIndexOptions { Unique = true }));
    }

    public Task<MLModel?> FindByNameAsync(string name, CancellationToken ct = default)
        => Col.Find(m => m.Name == name).FirstOrDefaultAsync(ct)!;

    public async Task<IReadOnlyList<MLModel>> ListEnabledAsync(CancellationToken ct = default)
        => await Col.Find(m => m.Enabled).ToListAsync(ct).ConfigureAwait(false);
}
