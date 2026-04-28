using System.Collections.Concurrent;
using SafeView.Application.Abstractions.Detection;
using SafeView.Domain.Cameras;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection;

/// <summary>
/// Centroid tracker w przestrzeni metrycznej. Per kamera trzyma listę aktywnych tracków.
/// Każdy track to deque ostatnich N próbek <c>(timestamp, floor_x_m, floor_y_m)</c> + klasa
/// (modelId, label). Przy każdej klatce:
///   1. Rzutuje stopę bboxa przez homografię → metry.
///   2. Greedy match do najbliższego tracka tej samej klasy w promieniu (m).
///   3. Brak matcha → nowy track.
///   4. Tracki nie zaktualizowane od TTL → eviction.
/// Velocity: różnica między najnowszą a najstarszą próbką w oknie / dt.
/// </summary>
public sealed class ObjectTracker : IObjectTracker
{
    // ── Config (sensowne defaulty pod 1 fps; opcjonalnie wystawić jako settings później) ──
    private const double MaxAssociationDistanceMeters = 5.0;  // pieszy 1.4 m/s, wózek do 5 m/s
    private const int VelocityWindowSamples = 3;              // ~2s przy 1 fps
    private const int MaxSamplesPerTrack = 6;                 // ringbuffer cap
    private static readonly TimeSpan TrackTtl = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, CameraTrackState> _state = new();

    public IReadOnlyDictionary<DetectionResult, TrackedInfo> UpdateAndEnrich(
        string cameraId,
        DateTime frameTimestamp,
        IReadOnlyList<DetectionResult> detections,
        HomographyMatrix? homography)
    {
        var result = new Dictionary<DetectionResult, TrackedInfo>(ReferenceEqualityComparer.Instance);
        if (homography is null || detections.Count == 0) return result;

        var camState = _state.GetOrAdd(cameraId, _ => new CameraTrackState());
        lock (camState.Sync)
        {
            // 1. Eviction starych tracków.
            var ttlCutoff = frameTimestamp - TrackTtl;
            for (int i = camState.Tracks.Count - 1; i >= 0; i--)
            {
                if (camState.Tracks[i].LastSeenUtc < ttlCutoff)
                    camState.Tracks.RemoveAt(i);
            }

            // 2. Rzut wszystkich detekcji na podłogę.
            var projected = new List<(DetectionResult Det, double X, double Y)?>(detections.Count);
            foreach (var d in detections)
            {
                var px = d.Bbox.X + d.Bbox.Width / 2.0;
                var py = d.Bbox.Y + d.Bbox.Height; // foot
                var floor = homography.Project(px, py);
                projected.Add(floor.HasValue ? (d, floor.Value.X, floor.Value.Y) : null);
            }

            // 3. Greedy matching — dla każdej (rzutowanej) detekcji szukamy najbliższego
            //    NIEDOPASOWANEGO już tracka tej samej klasy w promieniu.
            var assignedTracks = new HashSet<int>();
            for (int i = 0; i < projected.Count; i++)
            {
                if (projected[i] is not { } p) continue;

                int bestIdx = -1;
                double bestDist = MaxAssociationDistanceMeters;
                for (int t = 0; t < camState.Tracks.Count; t++)
                {
                    if (assignedTracks.Contains(t)) continue;
                    var tr = camState.Tracks[t];
                    if (tr.ModelId != p.Det.ModelId) continue;
                    if (!string.Equals(tr.Label, p.Det.Label, StringComparison.OrdinalIgnoreCase)) continue;

                    var (lastX, lastY, _) = tr.Samples[^1];
                    var dx = lastX - p.X;
                    var dy = lastY - p.Y;
                    var dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist <= bestDist)
                    {
                        bestDist = dist;
                        bestIdx = t;
                    }
                }

                Track track;
                if (bestIdx >= 0)
                {
                    track = camState.Tracks[bestIdx];
                    AppendSample(track, p.X, p.Y, frameTimestamp);
                    assignedTracks.Add(bestIdx);
                }
                else
                {
                    track = new Track(
                        Id: Guid.NewGuid().ToString("N"),
                        ModelId: p.Det.ModelId,
                        Label: p.Det.Label);
                    AppendSample(track, p.X, p.Y, frameTimestamp);
                    camState.Tracks.Add(track);
                }

                result[p.Det] = BuildInfo(track, p.X, p.Y);
            }
        }

        return result;
    }

    public void Reset() => _state.Clear();

    private static void AppendSample(Track track, double x, double y, DateTime t)
    {
        track.Samples.Add((x, y, t));
        if (track.Samples.Count > MaxSamplesPerTrack)
            track.Samples.RemoveAt(0);
        track.LastSeenUtc = t;
    }

    /// <summary>
    /// Liczy velocity z okna ostatnich <see cref="VelocityWindowSamples"/> próbek metodą
    /// (latest − oldest) / dt. Daje smoothing nad wolnymi kompromisami od KF i jest stabilne
    /// przy 1 fps. Heading w stopniach (atan2), zwracany przez <see cref="MotionMath.HeadingFromVelocity"/>.
    /// </summary>
    private static TrackedInfo BuildInfo(Track track, double currentX, double currentY)
    {
        if (track.Samples.Count < 2)
        {
            return new TrackedInfo(
                TrackId: track.Id,
                SamplesInTrack: track.Samples.Count,
                FloorX: currentX,
                FloorY: currentY,
                SpeedMps: null,
                HeadingDegrees: null);
        }

        var startIdx = Math.Max(0, track.Samples.Count - VelocityWindowSamples);
        var (sx, sy, st) = track.Samples[startIdx];
        var (ex, ey, et) = track.Samples[^1];
        var dt = (et - st).TotalSeconds;
        if (dt <= 0)
        {
            return new TrackedInfo(track.Id, track.Samples.Count, currentX, currentY, null, null);
        }

        var vx = (ex - sx) / dt;
        var vy = (ey - sy) / dt;
        var speed = Math.Sqrt(vx * vx + vy * vy);
        var heading = MotionMath.HeadingFromVelocity(vx, vy);

        return new TrackedInfo(
            TrackId: track.Id,
            SamplesInTrack: track.Samples.Count,
            FloorX: currentX,
            FloorY: currentY,
            SpeedMps: speed,
            HeadingDegrees: heading);
    }

    private sealed class CameraTrackState
    {
        public List<Track> Tracks { get; } = [];
        public object Sync { get; } = new();
    }

    private sealed record Track(string Id, string ModelId, string Label)
    {
        public List<(double X, double Y, DateTime Time)> Samples { get; } = [];
        public DateTime LastSeenUtc { get; set; }
    }
}
