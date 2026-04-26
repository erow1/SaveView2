using FluentAssertions;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Domain.Tests.Detection.Geometry;

public class NonMaxSuppressionTests
{
    private static NonMaxSuppression.NmsCandidate C(int classId, double conf, double x, double y, double w, double h, int origIdx = 0)
        => new(classId, conf, new Bbox(x, y, w, h), origIdx);

    [Fact]
    public void Apply_ReturnsEmpty_ForEmptyInput()
        => NonMaxSuppression.Apply([]).Should().BeEmpty();

    [Fact]
    public void Apply_ReturnsSingle_ForSingleInput()
    {
        var result = NonMaxSuppression.Apply([C(0, 0.9, 0, 0, 100, 100)]);
        result.Should().HaveCount(1);
    }

    [Fact]
    public void Apply_RemovesDuplicate_KeepsHigherConfidence()
    {
        // Dwa prawie identyczne bboxy, różne confidence → zostaje z wyższą
        var input = new List<NonMaxSuppression.NmsCandidate>
        {
            C(0, 0.7, 100, 100, 200, 200),
            C(0, 0.9, 105, 105, 195, 195), // overlap ~96%, wyższa confidence
        };
        var result = NonMaxSuppression.Apply(input);

        result.Should().HaveCount(1);
        result[0].Confidence.Should().Be(0.9);
    }

    [Fact]
    public void Apply_KeepsBoth_WhenBboxesFarApart()
    {
        // Dwa rozłączne bboxy — obie zachowane
        var input = new List<NonMaxSuppression.NmsCandidate>
        {
            C(0, 0.9, 0, 0, 100, 100),
            C(0, 0.8, 500, 500, 100, 100),
        };
        var result = NonMaxSuppression.Apply(input);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Apply_KeepsBoth_WhenDifferentClasses()
    {
        // Ten sam obszar, różne klasy (np. person i helmet) → obie zachowane
        var input = new List<NonMaxSuppression.NmsCandidate>
        {
            C(0, 0.9, 100, 100, 200, 200), // person
            C(1, 0.8, 105, 105, 195, 195), // helmet — NMS per-class
        };
        var result = NonMaxSuppression.Apply(input);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Apply_RespectsIouThreshold()
    {
        // Overlap ~60% — przy threshold=0.5 zredukowane, przy 0.7 zachowane
        var input = new List<NonMaxSuppression.NmsCandidate>
        {
            C(0, 0.9, 0, 0, 100, 100),   // area=10000
            C(0, 0.8, 40, 40, 100, 100), // overlap od (40,40) do (100,100) = 60*60=3600
                                          // union = 10000+10000-3600 = 16400
                                          // IoU = 3600/16400 ≈ 0.22
        };

        NonMaxSuppression.Apply(input, iouThreshold: 0.5).Should().HaveCount(2);
        NonMaxSuppression.Apply(input, iouThreshold: 0.1).Should().HaveCount(1);
    }

    [Fact]
    public void Apply_ChainOfOverlapping_ReducesToSingle()
    {
        // Trzy overlapujące się bboxy w łańcuchu — NMS zostawi top (0.95)
        var input = new List<NonMaxSuppression.NmsCandidate>
        {
            C(0, 0.7, 0, 0, 100, 100),
            C(0, 0.95, 5, 5, 100, 100),
            C(0, 0.8, 10, 10, 100, 100),
        };
        var result = NonMaxSuppression.Apply(input);
        result.Should().HaveCount(1);
        result[0].Confidence.Should().Be(0.95);
    }

    [Fact]
    public void IoU_Returns1_ForIdenticalBoxes()
    {
        var b = new Bbox(10, 20, 30, 40);
        NonMaxSuppression.IoU(b, b).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void IoU_Returns0_ForDisjointBoxes()
    {
        var a = new Bbox(0, 0, 10, 10);
        var b = new Bbox(100, 100, 10, 10);
        NonMaxSuppression.IoU(a, b).Should().Be(0);
    }

    [Fact]
    public void IoU_HalfOverlap_Returns1Third()
    {
        // a i b o pole 100 każdy, intersection = 50, union = 100+100-50 = 150 → IoU = 50/150 = 0.333
        var a = new Bbox(0, 0, 10, 10);
        var b = new Bbox(5, 0, 10, 10);
        NonMaxSuppression.IoU(a, b).Should().BeApproximately(1.0 / 3.0, 0.01);
    }

    [Fact]
    public void Apply_Handles100Detections_InReasonableTime()
    {
        // Stress: 100 detekcji, częściowo overlapping
        var rng = new Random(42);
        var input = Enumerable.Range(0, 100).Select(i =>
            C(i % 3, 0.5 + rng.NextDouble() * 0.5,
              rng.Next(0, 500), rng.Next(0, 500),
              50 + rng.Next(0, 50), 50 + rng.Next(0, 50))).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = NonMaxSuppression.Apply(input);
        sw.Stop();

        // O(n²) więc 100*100=10000 iteracji — powinno trwać <5ms
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
        result.Should().NotBeEmpty();
        result.Count.Should().BeLessThanOrEqualTo(input.Count);
    }
}
