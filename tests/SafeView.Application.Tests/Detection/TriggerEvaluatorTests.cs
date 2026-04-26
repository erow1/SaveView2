using FluentAssertions;
using NSubstitute;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Time;
using SafeView.Application.Detection;
using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Application.Tests.Detection;

public class TriggerEvaluatorTests
{
    private const string ZoneA = "zone-a";
    private const string ModelPerson = "model-person";
    private const string ModelForklift = "model-forklift";

    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TriggerEvaluator _evaluator;

    public TriggerEvaluatorTests()
    {
        _evaluator = new TriggerEvaluator(_clock);
        _clock.UtcNow.Returns(new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc));
    }

    private static Trigger BasicTrigger(int cooldown = 0, int persistent = 1, TriggerSchedule? schedule = null) =>
        new()
        {
            Id = "trigger-1",
            Name = "test",
            Enabled = true,
            CooldownSeconds = cooldown,
            PersistentFrames = persistent,
            Schedule = schedule,
            Conditions = [new TriggerCondition { ModelId = ModelPerson, Labels = ["person"], MinCount = 1 }]
        };

    private static DetectionResult Person(double confidence = 0.9) =>
        new(ModelPerson, "person", confidence, new Bbox(0.4, 0.4, 0.2, 0.2));

    private static DetectionResult Forklift(double confidence = 0.85) =>
        new(ModelForklift, "forklift", confidence, new Bbox(0.1, 0.1, 0.3, 0.3));

    // ─── Basic conditions ──────────────────────────────────────────────────

    [Fact]
    public void Fires_When_SingleConditionMet()
    {
        var trigger = BasicTrigger();
        var result = _evaluator.Evaluate(trigger, ZoneA, [Person()]);
        result.Fired.Should().BeTrue();
    }

    [Fact]
    public void DoesNotFire_When_NoDetectionsInZone()
    {
        var trigger = BasicTrigger();
        var result = _evaluator.Evaluate(trigger, ZoneA, []);
        result.Fired.Should().BeFalse();
    }

    [Fact]
    public void DoesNotFire_When_WrongModel()
    {
        var trigger = BasicTrigger();
        var result = _evaluator.Evaluate(trigger, ZoneA, [Forklift()]); // model mismatch
        result.Fired.Should().BeFalse();
    }

    [Fact]
    public void DoesNotFire_When_WrongLabel()
    {
        var trigger = BasicTrigger();
        var detection = new DetectionResult(ModelPerson, "dog", 0.9, new Bbox(0.5, 0.5, 0.1, 0.1));
        var result = _evaluator.Evaluate(trigger, ZoneA, [detection]);
        result.Fired.Should().BeFalse();
    }

    [Fact]
    public void DoesNotFire_When_ConfidenceBelowThreshold()
    {
        var trigger = BasicTrigger();
        trigger.Conditions[0].MinConfidence = 0.8;
        var result = _evaluator.Evaluate(trigger, ZoneA, [Person(confidence: 0.7)]);
        result.Fired.Should().BeFalse();
    }

    // ─── AND logic between conditions ──────────────────────────────────────

    [Fact]
    public void Fires_When_BothConditionsMet_PersonAndForklift()
    {
        var trigger = BasicTrigger();
        trigger.Conditions.Add(new TriggerCondition
        {
            ModelId = ModelForklift,
            Labels = ["forklift"],
            MinCount = 1
        });

        var result = _evaluator.Evaluate(trigger, ZoneA, [Person(), Forklift()]);
        result.Fired.Should().BeTrue();
    }

    [Fact]
    public void DoesNotFire_When_OnlyOneOfTwoConditionsMet()
    {
        var trigger = BasicTrigger();
        trigger.Conditions.Add(new TriggerCondition
        {
            ModelId = ModelForklift,
            Labels = ["forklift"],
            MinCount = 1
        });

        var result = _evaluator.Evaluate(trigger, ZoneA, [Person()]); // brak forklifta
        result.Fired.Should().BeFalse();
    }

    [Fact]
    public void Respects_MinCount_ForMultipleObjectsRequirement()
    {
        var trigger = BasicTrigger();
        trigger.Conditions[0].MinCount = 3;

        var result2 = _evaluator.Evaluate(trigger, ZoneA, [Person(), Person()]);
        result2.Fired.Should().BeFalse();

        var result3 = _evaluator.Evaluate(trigger, ZoneA, [Person(), Person(), Person()]);
        result3.Fired.Should().BeTrue();
    }

    // ─── Cooldown ──────────────────────────────────────────────────────────

    [Fact]
    public void Cooldown_PreventsSecondFireWithinWindow()
    {
        var trigger = BasicTrigger(cooldown: 30);

        var first = _evaluator.Evaluate(trigger, ZoneA, [Person()]);
        first.Fired.Should().BeTrue();

        _clock.UtcNow.Returns(new DateTime(2026, 4, 20, 10, 0, 15, DateTimeKind.Utc));
        var second = _evaluator.Evaluate(trigger, ZoneA, [Person()]);

        second.Fired.Should().BeFalse();
        second.SkipReason.Should().Be(ActionSkipReason.Cooldown);
    }

    [Fact]
    public void Cooldown_AllowsFire_AfterWindowExpires()
    {
        var trigger = BasicTrigger(cooldown: 30);

        _evaluator.Evaluate(trigger, ZoneA, [Person()]); // first fires

        _clock.UtcNow.Returns(new DateTime(2026, 4, 20, 10, 0, 31, DateTimeKind.Utc));
        var result = _evaluator.Evaluate(trigger, ZoneA, [Person()]);

        result.Fired.Should().BeTrue();
    }

    [Fact]
    public void Cooldown_IsPerZone_NotGlobal()
    {
        var trigger = BasicTrigger(cooldown: 60);

        _evaluator.Evaluate(trigger, "zone-a", [Person()]).Fired.Should().BeTrue();

        // Inna strefa — osobny stan, cooldown się nie tyczy
        _evaluator.Evaluate(trigger, "zone-b", [Person()]).Fired.Should().BeTrue();
    }

    // ─── PersistentFrames ──────────────────────────────────────────────────

    [Fact]
    public void PersistentFrames_Require3ConsecutiveHits()
    {
        var trigger = BasicTrigger(persistent: 3);

        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeFalse(); // 1/3
        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeFalse(); // 2/3
        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeTrue();  // 3/3 fires
    }

    [Fact]
    public void PersistentFrames_ResetCounter_WhenConditionsFail()
    {
        var trigger = BasicTrigger(persistent: 3);

        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeFalse();  // 1/3
        _evaluator.Evaluate(trigger, ZoneA, []).Fired.Should().BeFalse();          // reset
        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeFalse();  // 1/3
        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeFalse();  // 2/3
        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeTrue();   // 3/3
    }

    [Fact]
    public void PersistentFrames_ResetsAfterFire_SoNextRequiresNHitsAgain()
    {
        var trigger = BasicTrigger(cooldown: 0, persistent: 2);

        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeFalse(); // 1/2
        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeTrue();  // 2/2 fires, reset

        // Kolejne odpalenie znów wymaga 2 z rzędu
        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeFalse(); // 1/2
        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeTrue();  // 2/2
    }

    // ─── Schedule ──────────────────────────────────────────────────────────

    [Fact]
    public void Schedule_SkipsOutsideWindow()
    {
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                          DayOfWeek.Thursday, DayOfWeek.Friday],
            StartMinute = 9 * 60,
            EndMinute = 17 * 60
        };
        var trigger = BasicTrigger(schedule: schedule);

        _clock.UtcNow.Returns(new DateTime(2026, 4, 20, 8, 0, 0, DateTimeKind.Utc)); // pon 08:00
        var result = _evaluator.Evaluate(trigger, ZoneA, [Person()]);

        result.Fired.Should().BeFalse();
        result.SkipReason.Should().Be(ActionSkipReason.Schedule);
    }

    [Fact]
    public void Schedule_FiresInsideWindow()
    {
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Monday],
            StartMinute = 9 * 60,
            EndMinute = 17 * 60
        };
        var trigger = BasicTrigger(schedule: schedule);

        _clock.UtcNow.Returns(new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc)); // pon 10:00
        var result = _evaluator.Evaluate(trigger, ZoneA, [Person()]);
        result.Fired.Should().BeTrue();
    }

    // ─── Edge cases ────────────────────────────────────────────────────────

    [Fact]
    public void DoesNotFire_WhenTriggerDisabled()
    {
        var trigger = BasicTrigger();
        trigger.Enabled = false;

        var result = _evaluator.Evaluate(trigger, ZoneA, [Person()]);
        result.Fired.Should().BeFalse();
        result.SkipReason.Should().Be(ActionSkipReason.TriggerDisabled);
    }

    [Fact]
    public void DoesNotFire_WhenNoConditions()
    {
        var trigger = BasicTrigger();
        trigger.Conditions.Clear();

        var result = _evaluator.Evaluate(trigger, ZoneA, [Person()]);
        result.Fired.Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var trigger = BasicTrigger(cooldown: 60);

        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeTrue();
        _evaluator.Reset();
        // Po reset cooldown jest wyzerowany, więc kolejne odpalenie w tym samym czasie przechodzi
        _evaluator.Evaluate(trigger, ZoneA, [Person()]).Fired.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_Throws_WhenTriggerNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Evaluate(null!, ZoneA, [Person()]));
    }
}
