using SafeView.Domain.Cameras;

namespace SafeView.Application.Abstractions.Media;

public sealed record SnapshotResult(
    bool Success,
    string? RelativePath,
    string? AbsolutePath,
    string? ErrorMessage,
    DateTime CapturedAt,
    long SizeBytes);

public sealed record ClipResult(
    bool Success,
    string? RelativePath,
    string? AbsolutePath,
    string? ErrorMessage,
    DateTime CapturedAt,
    TimeSpan Duration,
    long SizeBytes);

public interface ISnapshotService
{
    /// <summary>Pobiera jedną klatkę z kamery, zapisuje jako JPG poprzez IFileStore (FileKind.Frame).</summary>
    Task<SnapshotResult> CaptureAsync(Camera camera, CancellationToken ct = default);

    /// <summary>Nagrywa klip N sekund (MP4) do FileKind.Clip.</summary>
    Task<ClipResult> CaptureClipAsync(Camera camera, int durationSeconds, CancellationToken ct = default);

    /// <summary>Zwraca URL strumienia do odtwarzania w UI (HLS z MediaMTX lub null).</summary>
    string? ResolveStreamUrl(Camera camera);
}
