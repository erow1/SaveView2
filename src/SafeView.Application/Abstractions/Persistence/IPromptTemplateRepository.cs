using SafeView.Domain.Vllm;

namespace SafeView.Application.Abstractions.Persistence;

public interface IPromptTemplateRepository : IRepository<PromptTemplate>
{
    /// <summary>Szablony pogrupowane per kategoria, posortowane po nazwie.</summary>
    Task<IReadOnlyList<PromptTemplate>> ListByCategoryAsync(string category, CancellationToken ct = default);

    /// <summary>Szablon built-in po stabilnym kluczu (używany przez seeder do re-sync).</summary>
    Task<PromptTemplate?> GetByBuiltInKeyAsync(string key, CancellationToken ct = default);
}
