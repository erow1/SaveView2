using FluentAssertions;
using SafeView.Domain.Detection;

namespace SafeView.Domain.Tests.Detection;

public class TriggerScheduleTests
{
    [Fact]
    public void IsActiveAt_ReturnsTrue_WhenDayAndHourMatch()
    {
        // środa, 09:00-17:00
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Wednesday],
            StartMinute = 9 * 60,
            EndMinute = 17 * 60
        };
        var wed_10am = new DateTime(2026, 4, 22, 10, 0, 0, DateTimeKind.Utc);

        schedule.IsActiveAt(wed_10am).Should().BeTrue();
    }

    [Fact]
    public void IsActiveAt_ReturnsFalse_WhenWrongDay()
    {
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Monday],
            StartMinute = 0,
            EndMinute = 24 * 60,
        };
        var tue = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc);

        schedule.IsActiveAt(tue).Should().BeFalse();
    }

    [Fact]
    public void IsActiveAt_ReturnsFalse_BeforeStartHour()
    {
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Wednesday],
            StartMinute = 9 * 60,
            EndMinute = 17 * 60
        };
        var wed_8am = new DateTime(2026, 4, 22, 8, 0, 0, DateTimeKind.Utc);

        schedule.IsActiveAt(wed_8am).Should().BeFalse();
    }

    [Fact]
    public void IsActiveAt_ReturnsFalse_AtEndHourExclusive()
    {
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Wednesday],
            StartMinute = 9 * 60,
            EndMinute = 17 * 60 // 17:00 exclusive
        };
        var wed_5pm = new DateTime(2026, 4, 22, 17, 0, 0, DateTimeKind.Utc);

        schedule.IsActiveAt(wed_5pm).Should().BeFalse();
    }

    [Fact]
    public void IsActiveAt_HandlesOvernightWindow_Before_Midnight()
    {
        // 22:00 - 06:00 (przekroczenie północy)
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                          DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
            StartMinute = 22 * 60,
            EndMinute = 6 * 60
        };
        var mon_11pm = new DateTime(2026, 4, 20, 23, 0, 0, DateTimeKind.Utc);

        schedule.IsActiveAt(mon_11pm).Should().BeTrue();
    }

    [Fact]
    public void IsActiveAt_HandlesOvernightWindow_After_Midnight()
    {
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                          DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
            StartMinute = 22 * 60,
            EndMinute = 6 * 60
        };
        var tue_3am = new DateTime(2026, 4, 21, 3, 0, 0, DateTimeKind.Utc);

        schedule.IsActiveAt(tue_3am).Should().BeTrue();
    }

    [Fact]
    public void IsActiveAt_HandlesOvernightWindow_OutsideRange()
    {
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                          DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
            StartMinute = 22 * 60,
            EndMinute = 6 * 60
        };
        var tue_noon = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc);

        schedule.IsActiveAt(tue_noon).Should().BeFalse();
    }

    [Fact]
    public void IsActiveAt_ReturnsFalse_WhenNoDaysConfigured()
    {
        var schedule = new TriggerSchedule { DaysOfWeek = [], StartMinute = 0, EndMinute = 24 * 60 };
        schedule.IsActiveAt(DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void IsActiveAt_ReturnsFalse_WhenStartEqualsEnd()
    {
        var schedule = new TriggerSchedule
        {
            DaysOfWeek = [DayOfWeek.Monday],
            StartMinute = 9 * 60,
            EndMinute = 9 * 60 // pusty przedział
        };
        var mon_noon = new DateTime(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc);
        schedule.IsActiveAt(mon_noon).Should().BeFalse();
    }
}
