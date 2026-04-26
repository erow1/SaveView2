using FluentAssertions;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Detection;
using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Application.Tests.Detection;

public class TriggerConditionMatcherTests
{
    private const string ModelClassic = "model-classic";
    private const string ModelYoloWorld = "model-yoloworld";

    private static DetectionResult Det(string modelId, string label, double confidence = 0.9) =>
        new(modelId, label, confidence, new Bbox(0.1, 0.1, 0.2, 0.2));

    // ─── Legacy path (DetectionClassId = null) ─────────────────────────────

    [Fact]
    public void Legacy_Matches_WhenModelAndLabelAgree()
    {
        var cond = new TriggerCondition { ModelId = ModelClassic, Labels = ["person"] };
        var det = Det(ModelClassic, "person");

        TriggerConditionMatcher.Matches(cond, det, null, enforceMinConfidence: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Legacy_DoesNotMatch_WhenModelDiffers()
    {
        var cond = new TriggerCondition { ModelId = ModelClassic, Labels = ["person"] };
        var det = Det(ModelYoloWorld, "person");

        TriggerConditionMatcher.Matches(cond, det, null, enforceMinConfidence: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Legacy_Matches_WhenLabelsEmpty()
    {
        // Puste Labels = dowolny label z tego modelu
        var cond = new TriggerCondition { ModelId = ModelClassic, Labels = [] };
        var det = Det(ModelClassic, "forklift");

        TriggerConditionMatcher.Matches(cond, det, null, enforceMinConfidence: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Legacy_MinConfidence_EnforcedOnlyWhenRequested()
    {
        var cond = new TriggerCondition { ModelId = ModelClassic, Labels = ["person"], MinConfidence = 0.8 };
        var det = Det(ModelClassic, "person", confidence: 0.6);

        TriggerConditionMatcher.Matches(cond, det, null, enforceMinConfidence: false)
            .Should().BeTrue();
        TriggerConditionMatcher.Matches(cond, det, null, enforceMinConfidence: true)
            .Should().BeFalse();
    }

    // ─── DetectionClass ClosedSetBinding path ──────────────────────────────

    [Fact]
    public void ClosedSetBinding_Matches_WhenModelAndLabelAgree()
    {
        var klass = new DetectionClass
        {
            Id = "class-1",
            Kind = DetectionClassKind.ClosedSetBinding,
            ClosedSetModelId = ModelClassic,
            ClosedSetLabel = "person"
        };
        var cond = new TriggerCondition { DetectionClassId = "class-1" };
        var det = Det(ModelClassic, "person");

        var dict = new Dictionary<string, DetectionClass> { ["class-1"] = klass };
        TriggerConditionMatcher.Matches(cond, det, dict, enforceMinConfidence: false)
            .Should().BeTrue();
    }

    [Fact]
    public void ClosedSetBinding_DoesNotMatch_WhenBoundModelDiffers()
    {
        var klass = new DetectionClass
        {
            Id = "class-1",
            Kind = DetectionClassKind.ClosedSetBinding,
            ClosedSetModelId = ModelClassic,
            ClosedSetLabel = "person"
        };
        var cond = new TriggerCondition { DetectionClassId = "class-1" };
        var det = Det(ModelYoloWorld, "person");

        var dict = new Dictionary<string, DetectionClass> { ["class-1"] = klass };
        TriggerConditionMatcher.Matches(cond, det, dict, enforceMinConfidence: false)
            .Should().BeFalse();
    }

    [Fact]
    public void ClosedSetBinding_LabelMatchIsCaseInsensitive()
    {
        var klass = new DetectionClass
        {
            Id = "class-1",
            Kind = DetectionClassKind.ClosedSetBinding,
            ClosedSetModelId = ModelClassic,
            ClosedSetLabel = "Person"
        };
        var cond = new TriggerCondition { DetectionClassId = "class-1" };
        var det = Det(ModelClassic, "person");

        var dict = new Dictionary<string, DetectionClass> { ["class-1"] = klass };
        TriggerConditionMatcher.Matches(cond, det, dict, enforceMinConfidence: false)
            .Should().BeTrue();
    }

    // ─── DetectionClass Text path (open-vocab) ─────────────────────────────

    [Fact]
    public void Text_Matches_WhenDetectionLabelEqualsPrompt()
    {
        var klass = new DetectionClass
        {
            Id = "class-text",
            Kind = DetectionClassKind.Text,
            TextPrompt = "construction worker wearing hard hat"
        };
        var cond = new TriggerCondition { DetectionClassId = "class-text" };
        var det = Det(ModelYoloWorld, "construction worker wearing hard hat");

        var dict = new Dictionary<string, DetectionClass> { ["class-text"] = klass };
        TriggerConditionMatcher.Matches(cond, det, dict, enforceMinConfidence: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Text_DoesNotMatch_WhenPromptDoesNotEqualLabel()
    {
        var klass = new DetectionClass
        {
            Id = "class-text",
            Kind = DetectionClassKind.Text,
            TextPrompt = "person without hard hat"
        };
        var cond = new TriggerCondition { DetectionClassId = "class-text" };
        var det = Det(ModelYoloWorld, "person");

        var dict = new Dictionary<string, DetectionClass> { ["class-text"] = klass };
        TriggerConditionMatcher.Matches(cond, det, dict, enforceMinConfidence: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Text_FiltersByModelId_WhenConditionHasIt()
    {
        var klass = new DetectionClass
        {
            Id = "class-text",
            Kind = DetectionClassKind.Text,
            TextPrompt = "forklift truck"
        };
        var cond = new TriggerCondition
        {
            DetectionClassId = "class-text",
            ModelId = ModelYoloWorld // wymuszamy konkretny model
        };

        var detFromYoloWorld = Det(ModelYoloWorld, "forklift truck");
        var detFromClassic = Det(ModelClassic, "forklift truck");

        var dict = new Dictionary<string, DetectionClass> { ["class-text"] = klass };
        TriggerConditionMatcher.Matches(cond, detFromYoloWorld, dict, enforceMinConfidence: false)
            .Should().BeTrue();
        TriggerConditionMatcher.Matches(cond, detFromClassic, dict, enforceMinConfidence: false)
            .Should().BeFalse();
    }

    // ─── Fallback gdy słownik klas jest null / klasy brak ──────────────────

    [Fact]
    public void FallsBackToLegacy_WhenDetectionClassIdSetButClassMissing()
    {
        // Scenario: trigger wskazuje klasę ale pipeline nie załadował słownika (np. repo null).
        // Matcher używa legacy path (ModelId + Labels), nie rzuca wyjątku.
        var cond = new TriggerCondition
        {
            DetectionClassId = "non-existing",
            ModelId = ModelClassic,
            Labels = ["person"]
        };
        var det = Det(ModelClassic, "person");

        TriggerConditionMatcher.Matches(cond, det, detectionClasses: null, enforceMinConfidence: false)
            .Should().BeTrue();

        var emptyDict = new Dictionary<string, DetectionClass>();
        TriggerConditionMatcher.Matches(cond, det, emptyDict, enforceMinConfidence: false)
            .Should().BeTrue();
    }
}
