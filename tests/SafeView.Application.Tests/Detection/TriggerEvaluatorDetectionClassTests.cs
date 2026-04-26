using FluentAssertions;
using NSubstitute;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Time;
using SafeView.Application.Detection;
using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Application.Tests.Detection;

/// <summary>
/// End-to-end dla <see cref="TriggerEvaluator"/> z użyciem <see cref="DetectionClass"/> —
/// sprawdza że nowy overload <c>Evaluate(..., detectionClasses)</c> działa dla ClosedSetBinding
/// oraz Text kind, i że stary overload bez słownika zachowuje identyczne zachowanie (backward-compat).
/// </summary>
public class TriggerEvaluatorDetectionClassTests
{
    private const string ZoneA = "zone-a";
    private const string ModelClassic = "model-classic";
    private const string ModelYoloWorld = "model-yoloworld";

    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TriggerEvaluator _evaluator;

    public TriggerEvaluatorDetectionClassTests()
    {
        _evaluator = new TriggerEvaluator(_clock);
        _clock.UtcNow.Returns(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc));
    }

    private static DetectionResult Det(string modelId, string label, double confidence = 0.9) =>
        new(modelId, label, confidence, new Bbox(0.1, 0.1, 0.2, 0.2));

    [Fact]
    public void Fires_When_ClosedSetBindingClassMatches()
    {
        var klass = new DetectionClass
        {
            Id = "class-person",
            Kind = DetectionClassKind.ClosedSetBinding,
            ClosedSetModelId = ModelClassic,
            ClosedSetLabel = "person"
        };
        var trigger = new Trigger
        {
            Id = "t1",
            Enabled = true,
            Conditions = [new TriggerCondition { DetectionClassId = "class-person", MinCount = 1 }]
        };

        var dict = new Dictionary<string, DetectionClass> { ["class-person"] = klass };
        var result = _evaluator.Evaluate(trigger, ZoneA, [Det(ModelClassic, "person")], dict);

        result.Fired.Should().BeTrue();
    }

    [Fact]
    public void DoesNotFire_When_BoundClassReferencesDifferentModel()
    {
        var klass = new DetectionClass
        {
            Id = "class-person",
            Kind = DetectionClassKind.ClosedSetBinding,
            ClosedSetModelId = ModelClassic,
            ClosedSetLabel = "person"
        };
        var trigger = new Trigger
        {
            Id = "t1",
            Enabled = true,
            Conditions = [new TriggerCondition { DetectionClassId = "class-person", MinCount = 1 }]
        };

        // Detekcja z innego modelu — klasa ClosedSetBinding wymaga konkretnego ModelId
        var dict = new Dictionary<string, DetectionClass> { ["class-person"] = klass };
        var result = _evaluator.Evaluate(trigger, ZoneA, [Det(ModelYoloWorld, "person")], dict);

        result.Fired.Should().BeFalse();
    }

    [Fact]
    public void Fires_When_TextPromptClassMatches()
    {
        var klass = new DetectionClass
        {
            Id = "class-forklift",
            Kind = DetectionClassKind.Text,
            TextPrompt = "forklift truck"
        };
        var trigger = new Trigger
        {
            Id = "t1",
            Enabled = true,
            Conditions = [new TriggerCondition { DetectionClassId = "class-forklift", MinCount = 1 }]
        };

        var dict = new Dictionary<string, DetectionClass> { ["class-forklift"] = klass };
        // YOLO-World zwraca label = prompt ("forklift truck")
        var result = _evaluator.Evaluate(trigger, ZoneA, [Det(ModelYoloWorld, "forklift truck")], dict);

        result.Fired.Should().BeTrue();
    }

    [Fact]
    public void AndLogic_AcrossMixedConditions_LegacyAndClassBased()
    {
        // Scenariusz mieszany: warunek A używa legacy (ModelId+Labels), warunek B używa DetectionClassId.
        // Oba muszą być spełnione (AND) żeby trigger wypalił.
        var klass = new DetectionClass
        {
            Id = "class-hi-vis",
            Kind = DetectionClassKind.Text,
            TextPrompt = "person wearing high-visibility vest"
        };
        var trigger = new Trigger
        {
            Id = "t1",
            Enabled = true,
            Conditions =
            [
                new TriggerCondition { ModelId = ModelClassic, Labels = ["forklift"], MinCount = 1 },
                new TriggerCondition { DetectionClassId = "class-hi-vis", MinCount = 1 }
            ]
        };

        var dict = new Dictionary<string, DetectionClass> { ["class-hi-vis"] = klass };

        // Sama ciężarówka wózek — trigger nie wypala (brak klasy Text)
        _evaluator.Evaluate(trigger, ZoneA,
                [Det(ModelClassic, "forklift")], dict)
            .Fired.Should().BeFalse();

        // Wózek + pracownik w kamizelce — trigger wypala (oba warunki spełnione)
        _evaluator.Evaluate(trigger, "zone-other",
                [Det(ModelClassic, "forklift"), Det(ModelYoloWorld, "person wearing high-visibility vest")], dict)
            .Fired.Should().BeTrue();
    }

    [Fact]
    public void LegacyOverload_StillWorks_ForBackwardCompat()
    {
        // Stary overload (bez słownika klas) musi zachować identyczne zachowanie —
        // triggery nie zmigrowane do DetectionClassId nadal działają.
        var trigger = new Trigger
        {
            Id = "t1",
            Enabled = true,
            Conditions = [new TriggerCondition { ModelId = ModelClassic, Labels = ["person"], MinCount = 1 }]
        };

        _evaluator.Evaluate(trigger, ZoneA, [Det(ModelClassic, "person")])
            .Fired.Should().BeTrue();
    }

    [Fact]
    public void DetectionClassId_WithoutDictionary_FallsBackToLegacyFields()
    {
        // Trigger ma DetectionClassId ale pipeline nie załadował słownika (null) — matcher
        // leci legacy path (ModelId+Labels). Nie rzucamy wyjątku, zachowanie deterministyczne.
        var trigger = new Trigger
        {
            Id = "t1",
            Enabled = true,
            Conditions =
            [
                new TriggerCondition
                {
                    DetectionClassId = "class-missing",
                    ModelId = ModelClassic,
                    Labels = ["person"],
                    MinCount = 1
                }
            ]
        };

        _evaluator.Evaluate(trigger, ZoneA, [Det(ModelClassic, "person")], detectionClasses: null)
            .Fired.Should().BeTrue();
    }
}
