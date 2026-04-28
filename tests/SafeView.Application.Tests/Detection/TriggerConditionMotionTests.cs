using FluentAssertions;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Detection;
using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Application.Tests.Detection;

public class TriggerConditionMotionTests
{
    private const string Model = "yolov8";

    private static DetectionResult Det() => new(Model, "person", 0.9, new Bbox(0.2, 0.4, 0.1, 0.2));

    private static TriggerCondition CondPerson() => new() { ModelId = Model, Labels = ["person"] };

    private static Dictionary<DetectionResult, TrackedInfo> Tracks(DetectionResult d, TrackedInfo info)
    {
        var dict = new Dictionary<DetectionResult, TrackedInfo>(ReferenceEqualityComparer.Instance);
        dict[d] = info;
        return dict;
    }

    [Fact]
    public void NoMotionRule_AlwaysMatches()
    {
        var cond = CondPerson();
        var d = Det();
        // Bez tracka, bez reguły — match
        TriggerConditionMatcher.Matches(cond, d, null, false, allDetections: null, tracks: null)
            .Should().BeTrue();
    }

    [Fact]
    public void MotionRule_NoTrack_FailClosed()
    {
        // Reguła ruchu wymaga tracka; brak tracka → odrzucone
        var cond = CondPerson();
        cond.Motion = new MotionRule { MinSpeedMps = 0.5 };

        var d = Det();
        TriggerConditionMatcher.Matches(cond, d, null, false, null, tracks: null)
            .Should().BeFalse();
    }

    [Fact]
    public void MotionRule_TooFewSamples_DoesNotMatch()
    {
        var cond = CondPerson();
        cond.Motion = new MotionRule { MinTrackSamples = 3 };

        var d = Det();
        var info = new TrackedInfo("track-1", SamplesInTrack: 2, FloorX: 0, FloorY: 0,
            SpeedMps: 1.5, HeadingDegrees: 0);

        TriggerConditionMatcher.Matches(cond, d, null, false, null, Tracks(d, info))
            .Should().BeFalse();
    }

    [Fact]
    public void SpeedAboveMin_Matches()
    {
        var cond = CondPerson();
        cond.Motion = new MotionRule { MinSpeedMps = 1.0 };

        var d = Det();
        var info = new TrackedInfo("track-1", 3, 0, 0, SpeedMps: 1.5, HeadingDegrees: 90);

        TriggerConditionMatcher.Matches(cond, d, null, false, null, Tracks(d, info))
            .Should().BeTrue();
    }

    [Fact]
    public void SpeedBelowMin_DoesNotMatch()
    {
        var cond = CondPerson();
        cond.Motion = new MotionRule { MinSpeedMps = 1.0 };

        var d = Det();
        var info = new TrackedInfo("track-1", 3, 0, 0, SpeedMps: 0.3, HeadingDegrees: 90);

        TriggerConditionMatcher.Matches(cond, d, null, false, null, Tracks(d, info))
            .Should().BeFalse();
    }

    [Fact]
    public void SpeedAboveMax_DoesNotMatch()
    {
        var cond = CondPerson();
        cond.Motion = new MotionRule { MaxSpeedMps = 2.0 };

        var d = Det();
        var info = new TrackedInfo("track-1", 3, 0, 0, SpeedMps: 5.0, HeadingDegrees: 90);

        TriggerConditionMatcher.Matches(cond, d, null, false, null, Tracks(d, info))
            .Should().BeFalse();
    }

    [Fact]
    public void HeadingWithinTolerance_Matches()
    {
        // Expected 0° (+X), tolerancja 90° → wszystko od -90° do +90° (czyli odpalanie tylko na FRONT)
        var cond = CondPerson();
        cond.Motion = new MotionRule
        {
            ExpectedHeadingDegrees = 0,
            DirectionToleranceDegrees = 90
        };

        var d = Det();
        // Heading 30° — w tolerancji
        var info = new TrackedInfo("track-1", 3, 0, 0, SpeedMps: 1.0, HeadingDegrees: 30);

        TriggerConditionMatcher.Matches(cond, d, null, false, null, Tracks(d, info))
            .Should().BeTrue();
    }

    [Fact]
    public void HeadingReverse_OutsideTolerance_DoesNotMatch()
    {
        // Expected 0° (wzdłuż +X). Tolerancja 90° → reverse (180°) poza tolerancją.
        var cond = CondPerson();
        cond.Motion = new MotionRule
        {
            ExpectedHeadingDegrees = 0,
            DirectionToleranceDegrees = 90
        };

        var d = Det();
        var info = new TrackedInfo("track-1", 3, 0, 0, SpeedMps: 1.0, HeadingDegrees: 180);

        TriggerConditionMatcher.Matches(cond, d, null, false, null, Tracks(d, info))
            .Should().BeFalse();
    }

    [Fact]
    public void CounterFlow_Use_CounterTrigger()
    {
        // Klasyczny use-case: "wykryj osobę idącą pod prąd". Korytarz idzie wzdłuż +X (0°).
        // Trigger ma palić gdy ktoś idzie pod prąd, więc:
        //   ExpectedHeading = 180 (przeciwnie do flow), tolerancja 90 (+/- 90 od 180)
        //   = wszystko od 90 do 270 stopni jest "pod prąd".
        var cond = CondPerson();
        cond.Motion = new MotionRule
        {
            ExpectedHeadingDegrees = 180,
            DirectionToleranceDegrees = 90
        };

        var d = Det();

        // Osoba idąca pod prąd (heading 175°) → trigger powinien się odpalić
        var againstFlow = new TrackedInfo("a", 3, 0, 0, 1.0, 175);
        TriggerConditionMatcher.Matches(cond, d, null, false, null, Tracks(d, againstFlow))
            .Should().BeTrue();

        // Osoba idąca z prądem (heading 0°) → NIE odpala
        var withFlow = new TrackedInfo("b", 3, 0, 0, 1.0, 0);
        TriggerConditionMatcher.Matches(cond, d, null, false, null, Tracks(d, withFlow))
            .Should().BeFalse();
    }

    [Fact]
    public void HeadingNullButRuleHasExpected_DoesNotMatch()
    {
        // Stojący obiekt → heading null → reguła kierunkowa fails closed.
        var cond = CondPerson();
        cond.Motion = new MotionRule { ExpectedHeadingDegrees = 0, DirectionToleranceDegrees = 90 };

        var d = Det();
        var info = new TrackedInfo("track-1", 3, 0, 0, SpeedMps: 0.001, HeadingDegrees: null);

        TriggerConditionMatcher.Matches(cond, d, null, false, null, Tracks(d, info))
            .Should().BeFalse();
    }

    [Fact]
    public void CombinedSpeedAndDirection_BothMustHold()
    {
        var cond = CondPerson();
        cond.Motion = new MotionRule
        {
            MinSpeedMps = 1.0,
            ExpectedHeadingDegrees = 0,
            DirectionToleranceDegrees = 30
        };

        var d = Det();

        // Szybki + dobry kierunek → match
        TriggerConditionMatcher.Matches(cond, d, null, false, null,
            Tracks(d, new TrackedInfo("a", 3, 0, 0, 2.0, 10))).Should().BeTrue();

        // Szybki ale zły kierunek → no
        TriggerConditionMatcher.Matches(cond, d, null, false, null,
            Tracks(d, new TrackedInfo("b", 3, 0, 0, 2.0, 60))).Should().BeFalse();

        // Dobry kierunek ale za wolny → no
        TriggerConditionMatcher.Matches(cond, d, null, false, null,
            Tracks(d, new TrackedInfo("c", 3, 0, 0, 0.3, 10))).Should().BeFalse();
    }
}
