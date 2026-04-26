using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Vllm;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoPromptTemplateRepository : MongoRepositoryBase<PromptTemplate>, IPromptTemplateRepository
{
    public const string CollectionName = "prompt_templates";

    private readonly IPromptTemplateVersionRepository _versions;

    public MongoPromptTemplateRepository(IMongoContext ctx, IPromptTemplateVersionRepository versions)
        : base(ctx, CollectionName)
    {
        _versions = versions;
        Col.Indexes.CreateMany([
            new CreateIndexModel<PromptTemplate>(Builders<PromptTemplate>.IndexKeys.Ascending(t => t.Category)),
            new CreateIndexModel<PromptTemplate>(Builders<PromptTemplate>.IndexKeys.Ascending(t => t.BuiltInKey)),
            new CreateIndexModel<PromptTemplate>(Builders<PromptTemplate>.IndexKeys.Ascending(t => t.Name)),
        ]);
    }

    public async Task<IReadOnlyList<PromptTemplate>> ListByCategoryAsync(string category, CancellationToken ct = default)
        => await Col.Find(t => t.Category == category)
            .SortBy(t => t.Name)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<PromptTemplate?> GetByBuiltInKeyAsync(string key, CancellationToken ct = default)
        => await Col.Find(t => t.BuiltInKey == key).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Override: przed zapisem nowej wersji szablonu zapisuje snapshot POPRZEDNIEGO stanu
    /// do <c>prompt_template_versions</c>. Pozwala na przegląd historii + rollback w UI.
    /// Failure snapshotu logowany jest niegłośno — update szablonu nie jest blokowany
    /// (user nie powinien tracić pracy przez problem z historią).
    /// </summary>
    public override async Task UpdateAsync(PromptTemplate entity, CancellationToken ct = default)
    {
        try
        {
            var prev = await GetByIdAsync(entity.Id, ct).ConfigureAwait(false);
            if (prev is not null)
            {
                await _versions.InsertAsync(new PromptTemplateVersion
                {
                    TemplateId = prev.Id,
                    Name = prev.Name,
                    Description = prev.Description,
                    Category = prev.Category,
                    SystemPrompt = prev.SystemPrompt,
                    UserTemplate = prev.UserTemplate,
                    SchemaJson = prev.SchemaJson,
                    RecommendedMinConfidence = prev.RecommendedMinConfidence,
                    DefaultMinSeverity = prev.DefaultMinSeverity,
                    RequireGoodImageQuality = prev.RequireGoodImageQuality
                }, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // snapshot best-effort — nie blokujemy update na problemach z versions collection
        }
        await base.UpdateAsync(entity, ct).ConfigureAwait(false);
    }
}
