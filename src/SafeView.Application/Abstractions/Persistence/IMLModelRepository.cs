using SafeView.Domain.ML;

namespace SafeView.Application.Abstractions.Persistence;

public interface IMLModelRepository : IRepository<MLModel>
{
    Task<MLModel?> FindByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<MLModel>> ListEnabledAsync(CancellationToken ct = default);
}
