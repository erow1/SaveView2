using FluentAssertions;
using SafeView.Domain.Cameras;
using Xunit;

namespace SafeView.Domain.Tests.Cameras;

public class HomographyTests
{
    [Fact]
    public void Compute_WithFewerThan4Points_ReturnsNull()
    {
        var pts = new List<CalibrationPoint>
        {
            new() { PixelX = 0, PixelY = 0, WorldX = 0, WorldY = 0 },
            new() { PixelX = 1, PixelY = 0, WorldX = 10, WorldY = 0 },
            new() { PixelX = 1, PixelY = 1, WorldX = 10, WorldY = 10 }
        };
        HomographyCalculator.Compute(pts).Should().BeNull();
    }

    [Fact]
    public void Compute_IdentityMapping_ProjectsPointToItself()
    {
        // 4 punkty mapujące bezpośrednio pixel→world 1:1 (bez perspektywy).
        var pts = new List<CalibrationPoint>
        {
            new() { PixelX = 0.0, PixelY = 0.0, WorldX = 0.0, WorldY = 0.0 },
            new() { PixelX = 1.0, PixelY = 0.0, WorldX = 1.0, WorldY = 0.0 },
            new() { PixelX = 1.0, PixelY = 1.0, WorldX = 1.0, WorldY = 1.0 },
            new() { PixelX = 0.0, PixelY = 1.0, WorldX = 0.0, WorldY = 1.0 }
        };
        var H = HomographyCalculator.Compute(pts);
        H.Should().NotBeNull();

        var (x, y) = H!.Project(0.5, 0.5)!.Value;
        x.Should().BeApproximately(0.5, 1e-6);
        y.Should().BeApproximately(0.5, 1e-6);
    }

    [Fact]
    public void Compute_ScalingMapping_ProjectsProportionally()
    {
        // Obraz [0..1]×[0..1] → podłoga [0..10m]×[0..5m]
        var pts = new List<CalibrationPoint>
        {
            new() { PixelX = 0.0, PixelY = 0.0, WorldX = 0, WorldY = 0 },
            new() { PixelX = 1.0, PixelY = 0.0, WorldX = 10, WorldY = 0 },
            new() { PixelX = 1.0, PixelY = 1.0, WorldX = 10, WorldY = 5 },
            new() { PixelX = 0.0, PixelY = 1.0, WorldX = 0, WorldY = 5 }
        };
        var H = HomographyCalculator.Compute(pts);
        H.Should().NotBeNull();

        var p = H!.Project(0.5, 0.5)!.Value;
        p.X.Should().BeApproximately(5.0, 1e-6);
        p.Y.Should().BeApproximately(2.5, 1e-6);

        var p2 = H.Project(0.8, 0.2)!.Value;
        p2.X.Should().BeApproximately(8.0, 1e-6);
        p2.Y.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public void Compute_PerspectiveMapping_RoundTripsKnownPoints()
    {
        // Kamera patrzy po skosie — dolna krawędź obrazu to bliższa podłogi (0..5m wszerz),
        // górna to dalsza (0..20m wszerz). Klasyczny trapez.
        var pts = new List<CalibrationPoint>
        {
            new() { PixelX = 0.1, PixelY = 0.9, WorldX = 0.0,  WorldY = 2.0 },  // bottom-left — 2m przed kamerą
            new() { PixelX = 0.9, PixelY = 0.9, WorldX = 5.0,  WorldY = 2.0 },  // bottom-right
            new() { PixelX = 0.0, PixelY = 0.1, WorldX = -7.5, WorldY = 10.0 }, // top-left — 10m, rozszerza się
            new() { PixelX = 1.0, PixelY = 0.1, WorldX = 12.5, WorldY = 10.0 }  // top-right
        };
        var H = HomographyCalculator.Compute(pts);
        H.Should().NotBeNull();

        // Każdy punkt powinien się rzutować z powrotem na swoją world-wartość.
        foreach (var p in pts)
        {
            var r = H!.Project(p.PixelX, p.PixelY)!.Value;
            r.X.Should().BeApproximately(p.WorldX, 1e-4);
            r.Y.Should().BeApproximately(p.WorldY, 1e-4);
        }
    }

    [Fact]
    public void Compute_CollinearPoints_ReturnsNull()
    {
        // Wszystkie 4 punkty na jednej linii → macierz osobliwa.
        var pts = new List<CalibrationPoint>
        {
            new() { PixelX = 0.0, PixelY = 0.5, WorldX = 0,  WorldY = 0 },
            new() { PixelX = 0.3, PixelY = 0.5, WorldX = 3,  WorldY = 0 },
            new() { PixelX = 0.6, PixelY = 0.5, WorldX = 6,  WorldY = 0 },
            new() { PixelX = 1.0, PixelY = 0.5, WorldX = 10, WorldY = 0 }
        };
        HomographyCalculator.Compute(pts).Should().BeNull();
    }
}
