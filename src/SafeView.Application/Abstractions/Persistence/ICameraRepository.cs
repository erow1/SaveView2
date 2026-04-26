using SafeView.Domain.Cameras;

namespace SafeView.Application.Abstractions.Persistence;

public interface ICameraRepository : IRepository<Camera>
{
    Task<Camera?> FindByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Camera>> ListEnabledAsync(CancellationToken ct = default);
}
