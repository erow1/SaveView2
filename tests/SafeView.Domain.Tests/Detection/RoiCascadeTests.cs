using FluentAssertions;
using SafeView.Domain.Detection;

namespace SafeView.Domain.Tests.Detection;

/// <summary>Testy domain invariantów ROI.ProposerModelId + threshold + padding (Faza 5).</summary>
public class RoiCascadeTests
{
    [Fact]
    public void Defaults_NormalModeWithoutCascade()
    {
        var roi = new Roi();
        roi.ProposerModelId.Should().BeNull();
        roi.ProposerConfidenceThreshold.Should().BeInRange(0, 1);
        roi.CascadeBboxPadding.Should().BeInRange(0, 1);
    }

    [Fact]
    public void Defaults_AreSane()
    {
        var roi = new Roi();
        roi.ProposerConfidenceThreshold.Should().Be(0.25,
            "default 25% threshold is low enough for recall but high enough to avoid noise");
        roi.CascadeBboxPadding.Should().Be(0.2,
            "default 20% padding gives confirmer reasonable context");
    }

    [Fact]
    public void Can_ActivateCascade()
    {
        var roi = new Roi
        {
            ProposerModelId = "yolov8n-proposer",
            ProposerConfidenceThreshold = 0.15,
            CascadeBboxPadding = 0.3,
            ModelIds = ["yolov8l-confirmer"]
        };

        roi.ProposerModelId.Should().NotBeNullOrEmpty();
        roi.ModelIds.Should().ContainSingle();
    }

    [Fact]
    public void Cascade_Validation_NeedsConfirmerToBeUseful()
    {
        // Proposer bez confirmer to zły setup — domain go na to pozwala,
        // pipeline radzi sobie (fallback: zwraca proposer detections jako wynik).
        var roi = new Roi
        {
            ProposerModelId = "yolov8n",
            ModelIds = [] // no confirmers
        };
        roi.ProposerModelId.Should().NotBeNull();
        roi.ModelIds.Should().BeEmpty();
        // Pipeline i tak to obsłuży — log + fallback na proposer detections.
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    [InlineData(0.5)]
    [InlineData(0.95)]
    public void Threshold_Accepts_FullRange(double t)
    {
        var roi = new Roi { ProposerConfidenceThreshold = t };
        roi.ProposerConfidenceThreshold.Should().Be(t);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Padding_Accepts_FullRange(double p)
    {
        var roi = new Roi { CascadeBboxPadding = p };
        roi.CascadeBboxPadding.Should().Be(p);
    }
}
