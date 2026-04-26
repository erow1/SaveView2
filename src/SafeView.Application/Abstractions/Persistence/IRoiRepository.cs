using SafeView.Domain.Detection;

namespace SafeView.Application.Abstractions.Persistence;

public interface IRoiRepository : IRepository<Roi>
{
    /// <summary>Wszystkie ROI (enabled + disabled) dla danej kamery.</summary>
    Task<IReadOnlyList<Roi>> ListByCameraAsync(string cameraId, CancellationToken ct = default);

    /// <summary>Tylko enabled ROI dla danej kamery — używane przez DetectionPipeline.</summary>
    Task<IReadOnlyList<Roi>> ListEnabledByCameraAsync(string cameraId, CancellationToken ct = default);
}
