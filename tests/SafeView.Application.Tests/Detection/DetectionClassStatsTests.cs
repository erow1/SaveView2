using FluentAssertions;
using SafeView.Application.Abstractions.Persistence;

namespace SafeView.Application.Tests.Detection;

/// <summary>
/// Unit testy dla <see cref="DetectionClassStats"/> record — sprawdzają semantykę
/// <see cref="DetectionClassStats.FalsePositiveRate"/> (division, edge case zero-fires).
/// Faktyczna logika agregacji Mongo (GetStatsByDetectionClassAsync) jest integracyjna
/// i weryfikowana przez uruchomienie z realnym MongoDB.
/// </summary>
public class DetectionClassStatsTests
{
    [Fact]
    public void FalsePositiveRate_Zero_WhenFireCountIsZero()
    {
        var stats = new DetectionClassStats(0, 0, null, null, new int[10]);
        stats.FalsePositiveRate.Should().Be(0);
    }

    [Fact]
    public void FalsePositiveRate_Zero_WhenNoFpButFiresExist()
    {
        var stats = new DetectionClassStats(FireCount: 100, FalsePositiveCount: 0,
            AvgConfidence: 0.85, LastFireAt: DateTime.UtcNow, ConfidenceHistogram: new int[10]);
        stats.FalsePositiveRate.Should().Be(0);
    }

    [Fact]
    public void FalsePositiveRate_HalfWhenEqualSplit()
    {
        var stats = new DetectionClassStats(FireCount: 20, FalsePositiveCount: 10,
            AvgConfidence: 0.5, LastFireAt: DateTime.UtcNow, ConfidenceHistogram: new int[10]);
        stats.FalsePositiveRate.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void FalsePositiveRate_HandlesNonRoundRatios()
    {
        var stats = new DetectionClassStats(FireCount: 7, FalsePositiveCount: 2,
            AvgConfidence: 0.4, LastFireAt: DateTime.UtcNow, ConfidenceHistogram: new int[10]);
        stats.FalsePositiveRate.Should().BeApproximately(2.0 / 7.0, 1e-9);
    }

    [Fact]
    public void ConfidenceHistogram_Has10Buckets()
    {
        // Kontrakt: histogram to zawsze 10 bucketów (0-10%, 10-20%, ..., 90-100%).
        // UI i MudChart polegają na tej długości.
        var stats = new DetectionClassStats(0, 0, null, null, new int[10]);
        stats.ConfidenceHistogram.Length.Should().Be(10);
    }
}
