using FluentAssertions;
using SafeView.Domain.Detection;

namespace SafeView.Domain.Tests.Detection;

public class MotionMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(720, 0)]
    [InlineData(-90, 270)]
    [InlineData(-180, 180)]
    [InlineData(450, 90)]
    [InlineData(45.5, 45.5)]
    public void NormalizeDegrees_WrapsToZeroToThreeSixty(double input, double expected)
    {
        MotionMath.NormalizeDegrees(input).Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 90, 90)]
    [InlineData(0, 270, -90)]    // 270° to nie 270 CCW od 0, tylko 90 CW (krótsza droga)
    [InlineData(350, 10, 20)]    // wraparound
    [InlineData(10, 350, -20)]
    [InlineData(180, 180, 0)]
    public void SignedDifferenceDegrees_WrapsToShortPath(double a, double b, double expected)
    {
        MotionMath.SignedDifferenceDegrees(a, b).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void SignedDifferenceDegrees_OppositeDirection_IsPlusOrMinusOneEighty()
    {
        // 180° różnicy to ambigualnie ±180 — obie wartości reprezentują "przeciwny kierunek".
        var diff = MotionMath.SignedDifferenceDegrees(0, 180);
        Math.Abs(diff).Should().BeApproximately(180, 1e-9);
    }

    [Theory]
    [InlineData(0, 180, 180)]    // odwrotny kierunek = 180
    [InlineData(0, 90, 90)]
    [InlineData(0, 270, 90)]     // CW 90 = absolutnie 90
    [InlineData(350, 10, 20)]
    [InlineData(10, 350, 20)]
    public void AbsoluteDifferenceDegrees_IsAlwaysInZeroToOneEighty(double a, double b, double expected)
    {
        var diff = MotionMath.AbsoluteDifferenceDegrees(a, b);
        diff.Should().BeApproximately(expected, 1e-9);
        diff.Should().BeInRange(0, 180);
    }

    [Fact]
    public void HeadingFromVelocity_ZeroVelocity_ReturnsNull()
    {
        MotionMath.HeadingFromVelocity(0, 0).Should().BeNull();
    }

    [Fact]
    public void HeadingFromVelocity_BelowMinMagnitude_ReturnsNull()
    {
        // Default minMagnitude = 1e-3
        MotionMath.HeadingFromVelocity(1e-4, 1e-4).Should().BeNull();
    }

    [Theory]
    [InlineData(1, 0, 0)]        // +X
    [InlineData(0, 1, 90)]       // +Y
    [InlineData(-1, 0, 180)]     // -X
    [InlineData(0, -1, 270)]     // -Y
    [InlineData(1, 1, 45)]       // diagonal NE
    [InlineData(-1, -1, 225)]    // diagonal SW
    public void HeadingFromVelocity_ReturnsCorrectAngle(double vx, double vy, double expected)
    {
        var h = MotionMath.HeadingFromVelocity(vx, vy);
        h.Should().NotBeNull();
        h!.Value.Should().BeApproximately(expected, 1e-6);
    }

    [Fact]
    public void HeadingFromVelocity_ScaleInvariant()
    {
        // Magnitude nie wpływa na heading (poza zerowym progiem).
        var h1 = MotionMath.HeadingFromVelocity(0.5, 0.5);
        var h2 = MotionMath.HeadingFromVelocity(5.0, 5.0);
        h1.Should().BeApproximately(h2!.Value, 1e-9);
    }
}
