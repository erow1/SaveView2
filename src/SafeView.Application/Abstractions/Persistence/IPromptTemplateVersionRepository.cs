using SafeView.Domain.Vllm;

namespace SafeView.Application.Abstractions.Persistence;

public interface IPromptTemplateVersionRepository : IRepository<PromptTemplateVersion>
{
    /// <summary>Historia wersji szablonu — najnowsze najpierw.</summary>
    Task<IReadOnlyList<PromptTemplateVersion>> ListByTemplateAsync(string templateId, CancellationToken ct = default);
}
