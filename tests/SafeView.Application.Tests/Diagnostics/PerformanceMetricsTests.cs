using FluentAssertions;
using NSubstitute;
using SafeView.Application.Abstractions.Diagnostics;
using SafeView.Application.Abstractions.Time;
using SafeView.Application.Diagnostics;

namespace SafeView.Application.Tests.Diagnostics;

public class PerformanceMetricsTests
{
    private readonly IClock _clock;
    private readonly PerformanceMetrics _metrics;
    private DateTime _now;

    public PerformanceMetricsTests()
    {
        _now = new DateTime(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc);
        _clock = Substitute.For<IClock>();
        _clock.UtcNow.Returns(_ => _now);
        _metrics = new PerformanceMetrics(_clock);
    }

    private PerformanceSample Sample(string stage = "pipeline", int durationMs = 100,
        string? cameraId = "cam-1", string? roiId = null, string? modelId = null,
        int detections = 1, bool success = true) =>
        new(_now, stage, cameraId, roiId, modelId, durationMs, detections, success);

    [Fact]
    public void Record_Empty_ReturnsEmptySnapshot()
    {
        var snap = _metrics.GetSnapshot(TimeSpan.FromMinutes(5));
        snap.TotalSamples.Should().Be(0);
        snap.TotalErrors.Should().Be(0);
        snap.FramesPerMinute.Should().Be(0);
        snap.AvgLatencyMs.Should().Be(0);
    }

    [Fact]
    public void Record_Aggregates_PipelineSamples()
    {
        _metrics.Record(Sample(durationMs: 100));
        _metrics.Record(Sample(durationMs: 200));
        _metrics.Record(Sample(durationMs: 300));

        var snap = _metrics.GetSnapshot(TimeSpan.FromMinutes(5));
        snap.TotalSamples.Should().Be(3);
        snap.AvgLatencyMs.Should().BeApproximately(200, 1);
        snap.FramesPerMinute.Should().BeApproximately(3.0 / 5.0, 0.01);
    }

    [Fact]
    public void Record_CountsErrors()
    {
        _metrics.Record(Sample(success: true));
        _metrics.Record(Sample(success: false));
        _metrics.Record(Sample(success: false));

        var snap = _metrics.GetSnapshot(TimeSpan.FromMinutes(5));
        snap.TotalErrors.Should().Be(2);
    }

    [Fact]
    public void P95_CalculatedCorrectly()
    {
        // 100 próbek, latencje 1..100 ms → P95 ≈ 95
        for (int i = 1; i <= 100; i++)
            _metrics.Record(Sample(durationMs: i));

        var snap = _metrics.GetSnapshot(TimeSpan.FromMinutes(5));
        snap.P95LatencyMs.Should().BeInRange(94, 96);
    }

    [Fact]
    public void Window_FiltersOldSamples()
    {
        // 1 próbka 10 minut temu, 1 teraz
        var old = new PerformanceSample(_now.AddMinutes(-10), "pipeline", "cam-1", null, null, 100, 1, true);
        _metrics.Record(old);
        _metrics.Record(Sample(durationMs: 200));

        // Window 5 minut — stara próbka poza oknem
        var snap5 = _metrics.GetSnapshot(TimeSpan.FromMinutes(5));
        snap5.TotalSamples.Should().Be(1);
        snap5.AvgLatencyMs.Should().Be(200);

        // Window 15 minut — obie pasują
        var snap15 = _metrics.GetSnapshot(TimeSpan.FromMinutes(15));
        snap15.TotalSamples.Should().Be(2);
    }

    [Fact]
    public void PerCamera_Aggregates_ByCamera()
    {
        _metrics.Record(Sample(cameraId: "cam-a", durationMs: 100));
        _metrics.Record(Sample(cameraId: "cam-a", durationMs: 200));
        _metrics.Record(Sample(cameraId: "cam-b", durationMs: 50));

        var snap = _metrics.GetSnapshot(TimeSpan.FromMinutes(5));
        snap.PerCamera.Should().HaveCount(2);

        var camA = snap.PerCamera.First(s => s.Key == "cam-a");
        camA.Count.Should().Be(2);
        camA.AvgLatencyMs.Should().BeApproximately(150, 1);

        var camB = snap.PerCamera.First(s => s.Key == "cam-b");
        camB.Count.Should().Be(1);
    }

    [Fact]
    public void PerModel_AggregatesCorrectly()
    {
        _metrics.Record(Sample(stage: "roi_inference", modelId: "yolov8n", cameraId: null, durationMs: 25, detections: 3));
        _metrics.Record(Sample(stage: "roi_inference", modelId: "yolov8n", cameraId: null, durationMs: 30, detections: 2));
        _metrics.Record(Sample(stage: "roi_inference", modelId: "yolov8l", cameraId: null, durationMs: 150, detections: 1));

        var snap = _metrics.GetSnapshot(TimeSpan.FromMinutes(5));
        snap.PerModel.Should().HaveCount(2);

        var n = snap.PerModel.First(s => s.Key == "yolov8n");
        n.Count.Should().Be(2);
        n.AvgDetections.Should().Be(2.5);
    }

    [Fact]
    public void PerStage_AggregatesCorrectly()
    {
        _metrics.Record(Sample(stage: "pipeline", durationMs: 300));
        _metrics.Record(Sample(stage: "proposer", cameraId: null, durationMs: 25));
        _metrics.Record(Sample(stage: "confirmer", cameraId: null, durationMs: 150));

        var snap = _metrics.GetSnapshot(TimeSpan.FromMinutes(5));
        snap.PerStage.Should().HaveCount(3);
        snap.PerStage.Select(s => s.Key).Should().BeEquivalentTo(["pipeline", "proposer", "confirmer"]);
    }

    [Fact]
    public void MaxWindow_IsClamped()
    {
        // Window 1 godziny → clamp do max 15 min
        var snap = _metrics.GetSnapshot(TimeSpan.FromHours(1));
        snap.Window.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Record_EvictsOldSamplesBeyondWindow()
    {
        // Rekord stary (20 min temu)
        _metrics.Record(new PerformanceSample(
            _now.AddMinutes(-20), "pipeline", "cam", null, null, 100, 1, true));

        // Nowy rekord — trigger cleanup
        _metrics.Record(Sample());

        // Stary powinien być evicted (MaxWindow 15 min)
        var snap = _metrics.GetSnapshot(TimeSpan.FromMinutes(15));
        snap.TotalSamples.Should().Be(1);
    }
}
