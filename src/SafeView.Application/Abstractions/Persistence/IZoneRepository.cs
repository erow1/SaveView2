using SafeView.Domain.Zones;

namespace SafeView.Application.Abstractions.Persistence;

public interface IZoneRepository : IRepository<Zone>
{
    Task<IReadOnlyList<Zone>> ListByCameraAsync(string cameraId, CancellationToken ct = default);
    Task<IReadOnlyList<Zone>> ListEnabledAsync(CancellationToken ct = default);
}
