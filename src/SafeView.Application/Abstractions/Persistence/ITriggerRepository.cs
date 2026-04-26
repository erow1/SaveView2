using SafeView.Domain.Detection;

namespace SafeView.Application.Abstractions.Persistence;

public interface ITriggerRepository : IRepository<Trigger>
{
    /// <summary>Triggery pasujące do podanych ID — używane przez DetectionPipeline.</summary>
    Task<IReadOnlyList<Trigger>> ListByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct = default);

    /// <summary>Pełna lista (UI /triggers).</summary>
    Task<IReadOnlyList<Trigger>> ListEnabledAsync(CancellationToken ct = default);
}
