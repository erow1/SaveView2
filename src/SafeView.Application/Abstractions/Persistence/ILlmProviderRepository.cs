using SafeView.Domain.Llm;

namespace SafeView.Application.Abstractions.Persistence;

public interface ILlmProviderRepository : IRepository<LlmProvider>
{
    /// <summary>Domyślny provider (IsDefault=true). Null gdy żaden nie oznaczony.</summary>
    Task<LlmProvider?> GetDefaultAsync(CancellationToken ct = default);
}
