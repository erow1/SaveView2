namespace SafeView.Domain.Detection;

/// <summary>
/// Okno czasowe w którym Trigger jest aktywny (dni tygodnia + godziny).
/// <c>null</c> na <see cref="Trigger.Schedule"/> = trigger aktywny 24/7.
/// </summary>
public sealed class TriggerSchedule
{
    /// <summary>Dni tygodnia kiedy Trigger jest aktywny. Pusta lista = żaden dzień (trigger wyłączony).</summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = [];

    /// <summary>Godzina startu okna (inclusive). Format HH:mm, zapisywane jako minuty od północy.</summary>
    public int StartMinute { get; set; }

    /// <summary>Godzina końca okna (exclusive). Gdy End &lt; Start → okno przekracza północ (np. 22:00-06:00).</summary>
    public int EndMinute { get; set; } = 24 * 60;

    /// <summary>Sprawdza czy dany moment wpada w okno schedule.</summary>
    public bool IsActiveAt(DateTime utcNow, TimeZoneInfo? tz = null)
    {
        var local = tz is null ? utcNow : TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);

        if (!DaysOfWeek.Contains(local.DayOfWeek)) return false;

        var nowMin = local.Hour * 60 + local.Minute;
        if (StartMinute == EndMinute) return false; // puste okno
        if (EndMinute > StartMinute)
            return nowMin >= StartMinute && nowMin < EndMinute;

        // okno przekracza północ (np. 22:00 → 06:00)
        return nowMin >= StartMinute || nowMin < EndMinute;
    }
}
