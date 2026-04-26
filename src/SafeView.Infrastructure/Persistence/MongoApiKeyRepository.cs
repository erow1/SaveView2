using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Api;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoApiKeyRepository : MongoRepositoryBase<ApiKey>, IApiKeyRepository
{
    public const string CollectionName = "api_keys";

    public MongoApiKeyRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateOne(new CreateIndexModel<ApiKey>(
            Builders<ApiKey>.IndexKeys.Ascending(k => k.KeyHash),
            new CreateIndexOptions { Unique = true }));
    }

    public Task<ApiKey?> FindByHashAsync(string keyHash, CancellationToken ct = default)
        => Col.Find(k => k.KeyHash == keyHash).FirstOrDefaultAsync(ct)!;

    public Task TouchUsedAsync(string id, DateTime usedAt, CancellationToken ct = default)
        => Col.UpdateOneAsync(k => k.Id == id,
            Builders<ApiKey>.Update.Set(k => k.LastUsedAt, usedAt), cancellationToken: ct);
}
