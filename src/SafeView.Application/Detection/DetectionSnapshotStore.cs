using System.Collections.Concurrent;
using SafeView.Application.Abstractions.Detection;

namespace SafeView.Application.Detection;

public sealed class DetectionSnapshotStore : IDetectionSnapshotStore
{
    private readonly ConcurrentDictionary<string, DetectionSnapshot> _snapshots = new();

    public void Update(DetectionSnapshot snapshot) => _snapshots[snapshot.CameraId] = snapshot;

    public DetectionSnapshot? GetLatest(string cameraId)
        => _snapshots.TryGetValue(cameraId, out var snap) ? snap : null;
}
