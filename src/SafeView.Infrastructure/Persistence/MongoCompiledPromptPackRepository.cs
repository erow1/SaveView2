using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Detection;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoCompiledPromptPackRepository : MongoRepositoryBase<CompiledPromptPack>, ICompiledPromptPackRepository
{
    public const string CollectionName = "compiled_prompt_packs";

    public MongoCompiledPromptPackRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateMany([
            new CreateIndexModel<CompiledPromptPack>(Builders<CompiledPromptPack>.IndexKeys.Ascending(p => p.SourceModelId)),
            new CreateIndexModel<CompiledPromptPack>(Builders<CompiledPromptPack>.IndexKeys.Ascending(p => p.Name)),
        ]);
    }

    public async Task<IReadOnlyList<CompiledPromptPack>> ListBySourceModelAsync(
        string sourceModelId, CancellationToken ct = default)
        => await Col.Find(p => p.SourceModelId == sourceModelId)
            .SortByDescending(p => p.CompiledAt)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<CompiledPromptPack?> FindMatchingAsync(
        string sourceModelId, string encoderBackend, IReadOnlyList<string> classIds,
        CancellationToken ct = default)
    {
        if (classIds.Count == 0) return null;
        // Dopasowanie order-insensitive: najpierw filtrujemy po modelu + encoderze + stale=false,
        // potem w pamięci sprawdzamy identyczność zbioru classIds.
        var candidates = await Col.Find(p =>
                p.SourceModelId == sourceModelId
                && p.EncoderBackend == encoderBackend
                && !p.Stale
                && p.ClassIds.Count == classIds.Count)
            .ToListAsync(ct).ConfigureAwait(false);

        var target = classIds.ToHashSet(StringComparer.Ordinal);
        return candidates.FirstOrDefault(p => p.ClassIds.Count == target.Count && p.ClassIds.All(target.Contains));
    }

    public async Task MarkStaleAsync(string packId, CancellationToken ct = default)
    {
        var update = Builders<CompiledPromptPack>.Update.Set(p => p.Stale, true);
        await Col.UpdateOneAsync(p => p.Id == packId, update, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<CompiledPromptPack?> FindByPromptsAsync(
        string sourceModelId, string encoderBackend, IReadOnlyList<string> prompts,
        CancellationToken ct = default)
    {
        if (prompts.Count == 0) return null;
        var candidates = await Col.Find(p =>
                p.SourceModelId == sourceModelId
                && p.EncoderBackend == encoderBackend
                && !p.Stale
                && p.Prompts.Count == prompts.Count)
            .ToListAsync(ct).ConfigureAwait(false);

        var target = prompts.ToHashSet(StringComparer.Ordinal);
        return candidates.FirstOrDefault(p => p.Prompts.Count == target.Count && p.Prompts.All(target.Contains));
    }
}
