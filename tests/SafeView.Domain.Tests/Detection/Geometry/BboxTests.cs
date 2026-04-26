using FluentAssertions;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Domain.Tests.Detection.Geometry;

public class BboxTests
{
    [Fact]
    public void Corners_ReturnsFourCornersInCorrectOrder()
    {
        var bbox = new Bbox(0.2, 0.3, 0.4, 0.5);
        var corners = bbox.Corners();

        corners.Should().HaveCount(4);
        // Używamy BeApproximately ze względu na precyzję IEEE-754 (0.2 + 0.4 ≠ 0.6 exactly)
        corners[0].X.Should().BeApproximately(0.2, 1e-9);
        corners[0].Y.Should().BeApproximately(0.3, 1e-9);
        corners[1].X.Should().BeApproximately(0.6, 1e-9);
        corners[1].Y.Should().BeApproximately(0.3, 1e-9);
        corners[2].X.Should().BeApproximately(0.6, 1e-9);
        corners[2].Y.Should().BeApproximately(0.8, 1e-9);
        corners[3].X.Should().BeApproximately(0.2, 1e-9);
        corners[3].Y.Should().BeApproximately(0.8, 1e-9);
    }

    [Fact]
    public void Center_IsMiddleOfBox()
    {
        var bbox = new Bbox(0.2, 0.3, 0.4, 0.5);
        bbox.CenterX.Should().Be(0.4);
        bbox.CenterY.Should().Be(0.55);
    }

    [Fact]
    public void Area_CalculatedCorrectly()
    {
        var bbox = new Bbox(0, 0, 0.5, 0.4);
        bbox.Area.Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void IntersectionArea_ReturnsOverlapArea()
    {
        var a = new Bbox(0, 0, 0.5, 0.5);
        var b = new Bbox(0.25, 0.25, 0.5, 0.5);
        // overlap: 0.25..0.5 w X i Y → 0.25 × 0.25 = 0.0625
        a.IntersectionArea(b).Should().BeApproximately(0.0625, 1e-9);
    }

    [Fact]
    public void IntersectionArea_ReturnsZeroForDisjointBoxes()
    {
        var a = new Bbox(0, 0, 0.3, 0.3);
        var b = new Bbox(0.5, 0.5, 0.3, 0.3);
        a.IntersectionArea(b).Should().Be(0);
    }

    [Fact]
    public void IntersectionArea_ReturnsZero_WhenBoxesTouchEdgeOnly()
    {
        var a = new Bbox(0, 0, 0.5, 0.5);
        var b = new Bbox(0.5, 0, 0.5, 0.5); // edge-to-edge, no overlap
        a.IntersectionArea(b).Should().Be(0);
    }

    [Fact]
    public void IntersectionArea_ReturnsSmallerArea_WhenOneContainsOther()
    {
        var outer = new Bbox(0, 0, 1, 1);
        var inner = new Bbox(0.25, 0.25, 0.5, 0.5);
        outer.IntersectionArea(inner).Should().BeApproximately(0.25, 1e-9);
    }
}
