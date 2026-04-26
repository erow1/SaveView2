using FluentAssertions;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Domain.Tests.Detection.Geometry;

public class PointInPolygonTests
{
    // Kwadrat jednostkowy
    private static readonly (double X, double Y)[] UnitSquare =
    [
        (0, 0), (1, 0), (1, 1), (0, 1),
    ];

    // Wielokąt niewypukły (kształt L)
    //  (0,0) ─── (1,0)
    //    │         │
    //    │       (1,0.5)
    //    │         └──(0.5,0.5)
    //    │                │
    //  (0,1)  ──── (0.5,1)
    private static readonly (double X, double Y)[] LShape =
    [
        (0, 0), (1, 0), (1, 0.5), (0.5, 0.5), (0.5, 1), (0, 1),
    ];

    [Fact]
    public void Contains_ReturnsTrue_WhenPointInsideSquare()
        => PointInPolygon.Contains(0.5, 0.5, UnitSquare).Should().BeTrue();

    [Fact]
    public void Contains_ReturnsFalse_WhenPointOutsideSquare()
        => PointInPolygon.Contains(1.5, 0.5, UnitSquare).Should().BeFalse();

    [Fact]
    public void Contains_ReturnsFalse_WhenPolygonHasLessThan3Points()
        => PointInPolygon.Contains(0.5, 0.5, [(0, 0), (1, 0)]).Should().BeFalse();

    [Fact]
    public void Contains_ReturnsFalse_WhenPolygonEmpty()
        => PointInPolygon.Contains(0.5, 0.5, []).Should().BeFalse();

    [Theory]
    [InlineData(0.25, 0.25, true)]  // wewnątrz dolnej-lewej części L
    [InlineData(0.25, 0.75, true)]  // wewnątrz pionowego ramienia L
    [InlineData(0.75, 0.75, false)] // w wyciętej części (niewypukły obszar)
    [InlineData(0.75, 0.25, true)]  // górna część L
    public void Contains_HandlesConcavePolygonCorrectly(double px, double py, bool expected)
        => PointInPolygon.Contains(px, py, LShape).Should().Be(expected);

    [Fact]
    public void Contains_Works_WithClockwiseOrdering()
    {
        // Ten sam kwadrat ale CW zamiast CCW
        var clockwise = new (double X, double Y)[] { (0, 0), (0, 1), (1, 1), (1, 0) };
        PointInPolygon.Contains(0.5, 0.5, clockwise).Should().BeTrue();
    }

    [Fact]
    public void Contains_ReturnsFalse_ForPointFarAway()
        => PointInPolygon.Contains(100, 100, UnitSquare).Should().BeFalse();

    [Theory]
    [InlineData(0.5, -0.01)] // tuż pod
    [InlineData(-0.01, 0.5)] // tuż obok
    [InlineData(1.01, 0.5)]  // tuż za
    public void Contains_ReturnsFalse_ForPointJustOutsideSquare(double px, double py)
        => PointInPolygon.Contains(px, py, UnitSquare).Should().BeFalse();
}
