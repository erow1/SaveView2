using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Llm;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoLlmProviderRepository : MongoRepositoryBase<LlmProvider>, ILlmProviderRepository
{
    public const string CollectionName = "llm_providers";

    public MongoLlmProviderRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateMany([
            new CreateIndexModel<LlmProvider>(Builders<LlmProvider>.IndexKeys.Ascending(p => p.IsDefault)),
            new CreateIndexModel<LlmProvider>(Builders<LlmProvider>.IndexKeys.Ascending(p => p.Name))
        ]);
    }

    public async Task<LlmProvider?> GetDefaultAsync(CancellationToken ct = default)
        => await Col.Find(p => p.IsDefault).FirstOrDefaultAsync(ct).ConfigureAwait(false);
}
