using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Vllm;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoPromptTemplateVersionRepository : MongoRepositoryBase<PromptTemplateVersion>, IPromptTemplateVersionRepository
{
    public const string CollectionName = "prompt_template_versions";

    public MongoPromptTemplateVersionRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateMany([
            new CreateIndexModel<PromptTemplateVersion>(Builders<PromptTemplateVersion>.IndexKeys
                .Ascending(v => v.TemplateId).Descending(v => v.CreatedAt)),
        ]);
    }

    public async Task<IReadOnlyList<PromptTemplateVersion>> ListByTemplateAsync(string templateId, CancellationToken ct = default)
        => await Col.Find(v => v.TemplateId == templateId)
            .SortByDescending(v => v.CreatedAt)
            .Limit(50) // sane cap — przy 50+ edycjach i tak nie pamięta się starych
            .ToListAsync(ct).ConfigureAwait(false);
}
