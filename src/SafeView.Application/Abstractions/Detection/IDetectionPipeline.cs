using SafeView.Domain.Cameras;

namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Orchestrator detekcji dla pojedynczej klatki kamery. Zastępuje legacy <c>DetectionFrameObserver</c>.
/// Wywoływany przez <c>CameraFrameSampler</c> po zrobieniu snapshot-a.
/// </summary>
public interface IDetectionPipeline
{
    /// <summary>
    /// Przetwarza jedną klatkę: pobiera ROI kamery → inferencja → matching Zone → Trigger → Action.
    /// Best-effort — błędy są logowane, ale nie propagują się do samplera (żeby nie zabić sekwencji
    /// klatek dla innych kamer).
    /// </summary>
    Task ProcessFrameAsync(Camera camera, string framePath, CancellationToken ct = default);
}
