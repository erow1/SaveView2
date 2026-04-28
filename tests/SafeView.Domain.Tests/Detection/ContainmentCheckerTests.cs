using FluentAssertions;
using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Domain.Tests.Detection;

public class ContainmentCheckerTests
{
    // Kontekst: outer = "osoba" (X=0.4, Y=0.4, W=0.2, H=0.4) — Right=0.6, Bottom=0.8.
    private static readonly Bbox Outer = new(0.4, 0.4, 0.2, 0.4);

    [Fact]
    public void Center_BboxFullyInside_Passes()
    {
        // Mały kask na głowie osoby
        var inner = new Bbox(0.45, 0.42, 0.08, 0.06);
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.Center).Should().BeTrue();
    }

    [Fact]
    public void Center_BboxCenterInside_PassesEvenIfPartiallyOutside()
    {
        // Kask wystaje nad głowę osoby (Y < outer.Y), ale center ciągle wewnątrz
        var inner = new Bbox(0.46, 0.38, 0.08, 0.06);
        var cy = 0.38 + 0.06 / 2.0;
        cy.Should().BeApproximately(0.41, 1e-9);
        cy.Should().BeGreaterThan(Outer.Y);
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.Center).Should().BeTrue();
    }

    [Fact]
    public void Center_BboxOutside_Fails()
    {
        // Kask zupełnie obok osoby
        var inner = new Bbox(0.1, 0.1, 0.05, 0.05);
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.Center).Should().BeFalse();
    }

    [Fact]
    public void Center_OnEdge_Passes()
    {
        // Center dokładnie na lewej krawędzi outer (X=0.4) — krawędź włączna (>=, <=)
        var inner = new Bbox(0.30, 0.50, 0.20, 0.05); // center = (0.40, 0.525)
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.Center).Should().BeTrue();
    }

    [Fact]
    public void IoMin_HighOverlap_Passes()
    {
        // Inner całkiem wewnątrz outer → intersection == area(inner) → IoMin == 1.0
        var inner = new Bbox(0.45, 0.5, 0.05, 0.05);
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.IoMin, 0.7).Should().BeTrue();
    }

    [Fact]
    public void IoMin_BelowThreshold_Fails()
    {
        // Inner wystaje poza outer w ~50% — IoMin == 0.5 < 0.7
        var inner = new Bbox(0.5, 0.5, 0.2, 0.05); // szerokość 0.2, połowa po prawej outer.Right=0.6
        // intersection: x∈[0.5..0.6] × y∈[0.5..0.55] = 0.1 × 0.05 = 0.005
        // area(inner) = 0.2 × 0.05 = 0.01
        // area(outer) = 0.2 × 0.4 = 0.08
        // IoMin = 0.005 / min(0.01, 0.08) = 0.005 / 0.01 = 0.5
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.IoMin, 0.7).Should().BeFalse();
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.IoMin, 0.4).Should().BeTrue();
    }

    [Fact]
    public void IoMin_NoOverlap_Fails()
    {
        var inner = new Bbox(0.0, 0.0, 0.1, 0.1);
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.IoMin, 0.7).Should().BeFalse();
    }

    [Fact]
    public void IoMin_ZeroAreaInner_Fails()
    {
        var inner = new Bbox(0.5, 0.5, 0.0, 0.0);
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.IoMin, 0.7).Should().BeFalse();
    }

    [Fact]
    public void Fully_BboxInside_Passes()
    {
        var inner = new Bbox(0.45, 0.5, 0.05, 0.05);
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.Fully).Should().BeTrue();
    }

    [Fact]
    public void Fully_BboxPartiallyOutside_Fails()
    {
        // Kask wystaje nad głowę (Y < outer.Y) — Fully blokuje, Center przepuściłby
        var inner = new Bbox(0.46, 0.38, 0.08, 0.06);
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.Fully).Should().BeFalse();
        ContainmentChecker.IsContained(inner, Outer, ContainmentCriterion.Center).Should().BeTrue();
    }

    [Fact]
    public void Fully_BboxEqualToOuter_Passes()
    {
        // Identyczne bbox-y — fully inside
        ContainmentChecker.IsContained(Outer, Outer, ContainmentCriterion.Fully).Should().BeTrue();
    }
}
