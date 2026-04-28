using FluentAssertions;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Detection;
using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Application.Tests.Detection;

public class TriggerConditionContainmentTests
{
    private const string ModelPersons = "model-persons";
    private const string ModelPpe = "model-ppe";

    // Two persons, side by side. Person A (left) wears a helmet. Person B (right) doesn't.
    private static readonly DetectionResult PersonA = new(ModelPersons, "person", 0.9,
        new Bbox(0.10, 0.30, 0.20, 0.40)); // X=0.10..0.30, Y=0.30..0.70
    private static readonly DetectionResult PersonB = new(ModelPersons, "person", 0.9,
        new Bbox(0.50, 0.30, 0.20, 0.40)); // X=0.50..0.70, Y=0.30..0.70

    // Helmet sitting on top of Person A's head (center inside A.bbox)
    private static readonly DetectionResult HelmetOnA = new(ModelPpe, "helmet", 0.85,
        new Bbox(0.15, 0.32, 0.10, 0.05));

    // Phone in person A's hand
    private static readonly DetectionResult PhoneOnA = new(ModelPpe, "phone", 0.7,
        new Bbox(0.16, 0.55, 0.04, 0.06));

    private static TriggerCondition PersonCond() => new()
    {
        ModelId = ModelPersons,
        Labels = ["person"]
    };

    [Fact]
    public void ContainsNone_PersonWithoutHelmet_Matches()
    {
        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsNone,
            Criterion = ContainmentCriterion.Center,
            OtherModelId = ModelPpe,
            OtherLabels = ["helmet"]
        };

        var all = new List<DetectionResult> { PersonA, PersonB, HelmetOnA };

        // PersonA HAS helmet → does NOT match (rule is "no helmet inside")
        TriggerConditionMatcher.Matches(cond, PersonA, null, false, all).Should().BeFalse();

        // PersonB has no helmet inside → matches
        TriggerConditionMatcher.Matches(cond, PersonB, null, false, all).Should().BeTrue();
    }

    [Fact]
    public void ContainsAny_PersonWithPhone_Matches()
    {
        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsAny,
            Criterion = ContainmentCriterion.Center,
            OtherModelId = ModelPpe,
            OtherLabels = ["phone"]
        };

        var all = new List<DetectionResult> { PersonA, PersonB, PhoneOnA };

        // PersonA has phone inside → matches
        TriggerConditionMatcher.Matches(cond, PersonA, null, false, all).Should().BeTrue();

        // PersonB has no phone inside → does not match
        TriggerConditionMatcher.Matches(cond, PersonB, null, false, all).Should().BeFalse();
    }

    [Fact]
    public void ContainsNone_NoOtherDetections_Matches()
    {
        // ContainsNone with empty insiders → trivially passes (vacuous truth)
        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsNone,
            Criterion = ContainmentCriterion.Center,
            OtherModelId = ModelPpe,
            OtherLabels = ["helmet"]
        };

        var all = new List<DetectionResult> { PersonA };

        TriggerConditionMatcher.Matches(cond, PersonA, null, false, all).Should().BeTrue();
    }

    [Fact]
    public void ContainsAny_NoOtherDetections_DoesNotMatch()
    {
        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsAny,
            Criterion = ContainmentCriterion.Center,
            OtherModelId = ModelPpe,
            OtherLabels = ["phone"]
        };

        var all = new List<DetectionResult> { PersonA };

        TriggerConditionMatcher.Matches(cond, PersonA, null, false, all).Should().BeFalse();
    }

    [Fact]
    public void OtherLabelsEmpty_AcceptsAnyLabelFromOtherModel()
    {
        // OtherLabels=[] means "any class from this model" — useful when single-class custom model
        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsAny,
            Criterion = ContainmentCriterion.Center,
            OtherModelId = ModelPpe,
            OtherLabels = []
        };

        var all = new List<DetectionResult> { PersonA, PhoneOnA };

        TriggerConditionMatcher.Matches(cond, PersonA, null, false, all).Should().BeTrue();
    }

    [Fact]
    public void DetectionClassReference_RoutesThroughClassResolution()
    {
        var helmetClass = new DetectionClass
        {
            Id = "class-helmet",
            Name = "Helmet",
            Kind = DetectionClassKind.ClosedSetBinding,
            ClosedSetModelId = ModelPpe,
            ClosedSetLabel = "helmet"
        };
        var classes = new Dictionary<string, DetectionClass> { ["class-helmet"] = helmetClass };

        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsNone,
            Criterion = ContainmentCriterion.Center,
            OtherDetectionClassId = "class-helmet"
        };

        var all = new List<DetectionResult> { PersonA, HelmetOnA };

        // PersonA has helmet (matched via class) → ContainsNone → does NOT match
        TriggerConditionMatcher.Matches(cond, PersonA, classes, false, all).Should().BeFalse();
    }

    [Fact]
    public void NullAllDetections_BypassesContainment()
    {
        // Defensive: when caller doesn't pass allDetections, containment is skipped
        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsAny,
            Criterion = ContainmentCriterion.Center,
            OtherModelId = ModelPpe,
            OtherLabels = ["phone"]
        };

        // Without allDetections, containment is ignored — falls back to base condition match
        TriggerConditionMatcher.Matches(cond, PersonA, null, false, allDetections: null)
            .Should().BeTrue();
    }

    [Fact]
    public void SubjectIsNotMatchedAgainstItself()
    {
        // Edge case: A's bbox technically contains itself by Center criterion. Make sure
        // we don't accidentally match a person against itself (would fire ContainsAny=person).
        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsAny,
            Criterion = ContainmentCriterion.Center,
            OtherModelId = ModelPersons,
            OtherLabels = ["person"]
        };

        var all = new List<DetectionResult> { PersonA };

        // Only PersonA — checking "person inside person" should NOT match itself
        TriggerConditionMatcher.Matches(cond, PersonA, null, false, all).Should().BeFalse();
    }

    [Fact]
    public void IoMinCriterion_AppliesThreshold()
    {
        // Big "person" bbox + small phone fully inside → IoMin=1.0 ≥ threshold
        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsAny,
            Criterion = ContainmentCriterion.IoMin,
            IoMinThreshold = 0.9,
            OtherModelId = ModelPpe,
            OtherLabels = ["phone"]
        };

        var all = new List<DetectionResult> { PersonA, PhoneOnA };

        TriggerConditionMatcher.Matches(cond, PersonA, null, false, all).Should().BeTrue();
    }

    [Fact]
    public void OtherModelDiffers_NotConsideredAsInsider()
    {
        // Helmet from a different model id → containment ignores it
        var helmetWrongModel = HelmetOnA with { ModelId = "model-other" };

        var cond = PersonCond();
        cond.Containment = new ContainmentRule
        {
            Operator = ContainmentOperator.ContainsNone,
            Criterion = ContainmentCriterion.Center,
            OtherModelId = ModelPpe,
            OtherLabels = ["helmet"]
        };

        var all = new List<DetectionResult> { PersonA, helmetWrongModel };

        // Helmet from "model-other" not counted → ContainsNone passes
        TriggerConditionMatcher.Matches(cond, PersonA, null, false, all).Should().BeTrue();
    }
}
