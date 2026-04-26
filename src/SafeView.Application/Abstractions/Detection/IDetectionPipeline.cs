using SafeView.Domain.Cameras;

namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Orchestrator detekcji dla pojedynczej klatki kamery. Zastępuje legacy <c>DetectionFrameObserver</c>.
/// Wywoływany przez <c>CameraFrameSampler</c> po zrobieniu snapshot-a (RTSP/HTTP/File) albo przez
/// <c>IngestEndpoints</c> dla kamer typu <see cref="CameraTransport.Api"/> (push-based).
/// </summary>
public interface IDetectionPipeline
{
    /// <summary>
    /// Przetwarza jedną klatkę: pobiera ROI kamery → inferencja → matching Zone → Trigger → Action.
    /// Best-effort — błędy są logowane, ale nie propagują się do samplera (żeby nie zabić sekwencji
    /// klatek dla innych kamer).
    /// </summary>
    Task ProcessFrameAsync(Camera camera, string framePath, CancellationToken ct = default);

    /// <summary>
    /// Push-based wariant dla kamer typu <see cref="CameraTransport.Api"/>:
    /// detekcje są dostarczone z zewnątrz (już znormalizowane do [0..1] w układzie klatki).
    /// Pomija stage inferencji — wpada od razu do <c>TriggerEvaluator</c> i <c>ActionDispatcher</c>.
    ///
    /// Downstream (VLLM gate, audit, monitor wall, flow events) widzi to jak normalną detekcję
    /// — nie wie że źródło jest zewnętrzne.
    ///
    /// <paramref name="capturedAt"/> to timestamp od sendera (czas faktycznej obserwacji).
    /// Jest używany dla <c>OccurredAt</c> w incidencie + snapshot store, zamiast <c>IClock.UtcNow</c>.
    /// </summary>
    Task ProcessExternalDetectionsAsync(
        Camera camera,
        string framePath,
        string? frameRelativePath,
        IReadOnlyList<DetectionResult> externalDetections,
        DateTime capturedAt,
        CancellationToken ct = default);
}
