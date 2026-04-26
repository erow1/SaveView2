namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Ostatnia klatka detekcji per kamera — zapisywana przez DetectionPipeline,
/// czytana przez podgląd w /monitor. Koordynaty są znormalizowane [0..1] kamery.
/// </summary>
public sealed record DetectionSnapshot(
    string CameraId,
    DateTime CapturedAt,
    IReadOnlyList<DetectionResult> Detections,
    string? FrameRelativePath = null);

/// <summary>
/// In-memory store ostatnich detekcji per kamera. Singleton, thread-safe.
/// Nie persistowany — po restarcie aplikacji resetuje się.
/// </summary>
public interface IDetectionSnapshotStore
{
    void Update(DetectionSnapshot snapshot);
    DetectionSnapshot? GetLatest(string cameraId);
}
