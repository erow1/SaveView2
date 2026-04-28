using FluentAssertions;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Detection;
using SafeView.Domain.Cameras;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Application.Tests.Detection;

public class ObjectTrackerTests
{
    // Identity-like homography: image [0..1] × 10 = floor metry [0..10].
    private static readonly HomographyMatrix Hom = new(
        10, 0, 0,
        0, 10, 0,
        0, 0, 1);

    private const string Cam = "cam-1";
    private const string Model = "yolov8";

    private static DetectionResult Det(string label, double x, double y, double w = 0.1, double h = 0.2)
        => new(Model, label, 0.9, new Bbox(x, y, w, h));

    private static DateTime T(int seconds) => new(2026, 4, 27, 12, 0, seconds, DateTimeKind.Utc);

    [Fact]
    public void FirstFrame_CreatesTrack_NoVelocityYet()
    {
        var tracker = new ObjectTracker();
        var person = Det("person", 0.20, 0.40);

        var info = tracker.UpdateAndEnrich(Cam, T(0), [person], Hom);

        info.Should().ContainKey(person);
        var t = info[person];
        t.SamplesInTrack.Should().Be(1);
        t.SpeedMps.Should().BeNull();
        t.HeadingDegrees.Should().BeNull();
    }

    [Fact]
    public void SecondFrame_NearbyDetection_AssignsSameTrack_ComputesVelocity()
    {
        var tracker = new ObjectTracker();

        // Klatka 0: person w (0.20, 0.40) → floor (2, 6) (foot=Y+H=0.6 → 6m)
        var p0 = Det("person", 0.20, 0.40);
        var t0 = tracker.UpdateAndEnrich(Cam, T(0), [p0], Hom);
        var trackId0 = t0[p0].TrackId;

        // Klatka 1 (1s później): person ruszył 0.10 w prawo → floor delta (1, 0) → 1 m/s, heading 0° (+X)
        var p1 = Det("person", 0.30, 0.40);
        var t1 = tracker.UpdateAndEnrich(Cam, T(1), [p1], Hom);

        t1.Should().ContainKey(p1);
        var info = t1[p1];
        info.TrackId.Should().Be(trackId0); // ten sam track
        info.SamplesInTrack.Should().Be(2);
        info.SpeedMps.Should().NotBeNull();
        info.SpeedMps!.Value.Should().BeApproximately(1.0, 1e-6);
        info.HeadingDegrees.Should().NotBeNull();
        info.HeadingDegrees!.Value.Should().BeApproximately(0.0, 1e-6); // wzdłuż +X
    }

    [Fact]
    public void DifferentLabel_DoesNotMatch_NewTrackCreated()
    {
        var tracker = new ObjectTracker();
        var person = Det("person", 0.20, 0.40);
        var forklift = Det("forklift", 0.20, 0.40); // ta sama pozycja, inna klasa

        var t0 = tracker.UpdateAndEnrich(Cam, T(0), [person], Hom);
        var t1 = tracker.UpdateAndEnrich(Cam, T(1), [forklift], Hom);

        t1[forklift].TrackId.Should().NotBe(t0[person].TrackId);
        t1[forklift].SamplesInTrack.Should().Be(1); // świeży track
    }

    [Fact]
    public void FarAwayDetection_DoesNotMatch_NewTrackCreated()
    {
        var tracker = new ObjectTracker();
        var p0 = Det("person", 0.10, 0.40); // floor (1, 6)
        var p1 = Det("person", 0.90, 0.40); // floor (9, 6) — 8m gap > 5m threshold

        var t0 = tracker.UpdateAndEnrich(Cam, T(0), [p0], Hom);
        var t1 = tracker.UpdateAndEnrich(Cam, T(1), [p1], Hom);

        t1[p1].TrackId.Should().NotBe(t0[p0].TrackId);
        t1[p1].SamplesInTrack.Should().Be(1);
    }

    [Fact]
    public void TtlEviction_OldTracksDropped()
    {
        var tracker = new ObjectTracker();
        var p0 = Det("person", 0.20, 0.40);
        tracker.UpdateAndEnrich(Cam, T(0), [p0], Hom);

        // Druga klatka >5s później (TTL=5s) — track ma być wyrzucony, nowy ma być stworzony
        // nawet dla pozycji która byłaby w promieniu.
        var p1 = Det("person", 0.21, 0.40);
        var t1 = tracker.UpdateAndEnrich(Cam, T(10), [p1], Hom);

        // Nowy track — bo poprzedni został evicted przed matchem.
        t1[p1].SamplesInTrack.Should().Be(1);
    }

    [Fact]
    public void NullHomography_ReturnsEmptyDict()
    {
        var tracker = new ObjectTracker();
        var person = Det("person", 0.5, 0.5);

        var info = tracker.UpdateAndEnrich(Cam, T(0), [person], homography: null);
        info.Should().BeEmpty();
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var tracker = new ObjectTracker();
        var p0 = Det("person", 0.2, 0.4);
        tracker.UpdateAndEnrich(Cam, T(0), [p0], Hom);
        tracker.Reset();

        // Po reset — drugi update zaczyna od 1 próbki (track ID inny niż gdyby się dopasowało).
        var p1 = Det("person", 0.21, 0.40);
        var t1 = tracker.UpdateAndEnrich(Cam, T(1), [p1], Hom);
        t1[p1].SamplesInTrack.Should().Be(1);
    }

    [Fact]
    public void VelocityWindow_UsesOldestSampleInWindow_NotPairwise()
    {
        // Track z 4 próbkami: x = 0.20 → 0.30 → 0.40 → 0.50 co 1s, każda klatka.
        // Window = 3 ostatnie próbki, czyli (0.30 → 0.50) / 2s = 0.10 / 2s = 0.05 obrazu/s
        // = 0.5 m/s przy 10× skali. Heading 0° (+X).
        var tracker = new ObjectTracker();
        var dets = new[]
        {
            Det("person", 0.20, 0.40),
            Det("person", 0.30, 0.40),
            Det("person", 0.40, 0.40),
            Det("person", 0.50, 0.40),
        };

        TrackedInfo last = null!;
        for (int i = 0; i < dets.Length; i++)
        {
            var r = tracker.UpdateAndEnrich(Cam, T(i), [dets[i]], Hom);
            last = r[dets[i]];
        }

        last.SamplesInTrack.Should().BeGreaterOrEqualTo(3);
        last.SpeedMps!.Value.Should().BeApproximately(1.0, 1e-6); // (0.50-0.30)*10 / 2s = 1 m/s
        last.HeadingDegrees!.Value.Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public void DifferentCameras_HaveSeparateState()
    {
        var tracker = new ObjectTracker();
        var p = Det("person", 0.20, 0.40);

        var c1 = tracker.UpdateAndEnrich("cam-A", T(0), [p], Hom);
        var c2 = tracker.UpdateAndEnrich("cam-B", T(0), [p], Hom);

        // Te same detekcje, dwie kamery → dwa osobne tracki
        c1[p].TrackId.Should().NotBe(c2[p].TrackId);
    }
}
