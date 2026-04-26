using FluentAssertions;
using SafeView.Domain.Detection;

namespace SafeView.Domain.Tests.Detection;

public class RoiRectangleTests
{
    [Fact]
    public void IsValid_ReturnsTrue_ForRectangleWithinBounds()
    {
        var r = new RoiRectangle { X = 0.1, Y = 0.1, Width = 0.5, Height = 0.5 };
        r.IsValid().Should().BeTrue();
    }

    [Fact]
    public void IsValid_ReturnsTrue_ForFullFrame()
    {
        var r = new RoiRectangle { X = 0, Y = 0, Width = 1, Height = 1 };
        r.IsValid().Should().BeTrue();
    }

    [Theory]
    [InlineData(-0.1, 0, 0.5, 0.5)] // X poza zakresem
    [InlineData(0, -0.1, 0.5, 0.5)] // Y poza zakresem
    [InlineData(0, 0, 0, 0.5)]      // zero width
    [InlineData(0, 0, 0.5, 0)]      // zero height
    [InlineData(0.5, 0.5, 0.6, 0.6)] // prostokąt wystaje poza 0..1
    public void IsValid_ReturnsFalse_ForInvalidRectangle(double x, double y, double w, double h)
    {
        var r = new RoiRectangle { X = x, Y = y, Width = w, Height = h };
        r.IsValid().Should().BeFalse();
    }

    [Fact]
    public void Contains_ReturnsTrue_WhenPointInside()
    {
        var r = new RoiRectangle { X = 0.2, Y = 0.2, Width = 0.6, Height = 0.6 };
        r.Contains(0.5, 0.5).Should().BeTrue();
    }

    [Fact]
    public void Contains_ReturnsFalse_WhenPointOutside()
    {
        var r = new RoiRectangle { X = 0.2, Y = 0.2, Width = 0.6, Height = 0.6 };
        r.Contains(0.1, 0.5).Should().BeFalse();
    }

    [Fact]
    public void Contains_ReturnsTrue_WhenPointOnBoundary()
    {
        var r = new RoiRectangle { X = 0.2, Y = 0.2, Width = 0.6, Height = 0.6 };
        r.Contains(0.2, 0.2).Should().BeTrue(); // corner
        r.Contains(0.8, 0.8).Should().BeTrue(); // opposite corner
    }

    [Fact]
    public void Right_Bottom_CalculatedCorrectly()
    {
        var r = new RoiRectangle { X = 0.2, Y = 0.3, Width = 0.4, Height = 0.5 };
        r.Right.Should().BeApproximately(0.6, 1e-9);
        r.Bottom.Should().BeApproximately(0.8, 1e-9);
    }
}
