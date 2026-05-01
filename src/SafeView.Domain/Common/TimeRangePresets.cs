namespace SafeView.Domain.Common;

/// <summary>
/// Predefiniowane okresy czasu — "ostatnia godzina", "bieżący miesiąc", "poprzedni rok" itp.
/// Używane przez UI raportów (i każdą inną stronę gdzie user wybiera zakres czasowy) żeby
/// nie musiał klikać 4 razy w kalendarz dla popularnych przypadków.
///
/// **Kluczowa konwencja**: granice tygodnia/miesiąca/kwartału/roku liczymy w **lokalnym
/// czasie** (tak jak BHP-owiec myśli o zmianie/dobie), ale wynik (FromUtc/ToUtc) zwracamy
/// w UTC — bo cała aplikacja przechowuje incydenty w UTC.
/// </summary>
public static class TimeRangePresets
{
    public enum PresetKey
    {
        LastHour,
        TodayFrom6,
        Last24h,
        Today,
        Yesterday,
        ThisWeek,
        Last7Days,
        PreviousWeek,
        ThisMonth,
        Last30Days,
        PreviousMonth,
        ThisQuarter,
        PreviousQuarter,
        ThisYear,
        PreviousYear
    }

    public static readonly PresetKey[] All =
    [
        PresetKey.LastHour, PresetKey.TodayFrom6, PresetKey.Last24h,
        PresetKey.Today, PresetKey.Yesterday,
        PresetKey.ThisWeek, PresetKey.Last7Days, PresetKey.PreviousWeek,
        PresetKey.ThisMonth, PresetKey.Last30Days, PresetKey.PreviousMonth,
        PresetKey.ThisQuarter, PresetKey.PreviousQuarter,
        PresetKey.ThisYear, PresetKey.PreviousYear
    ];

    /// <summary>Zwraca okres dla danego presetu w odniesieniu do podanego "teraz" (UTC).
    /// Przyjmując nowUtc zamiast czytać DateTime.UtcNow umożliwia testy deterministyczne.</summary>
    public static TimeRange Resolve(PresetKey key, DateTime nowUtc)
    {
        var localNow = nowUtc.ToLocalTime();
        return key switch
        {
            PresetKey.LastHour       => new TimeRange(nowUtc.AddHours(-1), nowUtc),
            PresetKey.TodayFrom6     => Local(localNow.Date.AddHours(6), localNow),
            PresetKey.Last24h        => new TimeRange(nowUtc.AddHours(-24), nowUtc),
            PresetKey.Today          => Local(localNow.Date, localNow),
            PresetKey.Yesterday      => Local(localNow.Date.AddDays(-1), localNow.Date),
            PresetKey.ThisWeek       => Local(StartOfWeek(localNow), localNow),
            PresetKey.Last7Days      => new TimeRange(nowUtc.AddDays(-7), nowUtc),
            PresetKey.PreviousWeek   => Local(StartOfWeek(localNow).AddDays(-7), StartOfWeek(localNow)),
            PresetKey.ThisMonth      => Local(StartOfMonth(localNow), localNow),
            PresetKey.Last30Days     => new TimeRange(nowUtc.AddDays(-30), nowUtc),
            PresetKey.PreviousMonth  => Local(StartOfMonth(localNow).AddMonths(-1), StartOfMonth(localNow)),
            PresetKey.ThisQuarter    => Local(StartOfQuarter(localNow), localNow),
            PresetKey.PreviousQuarter => Local(StartOfQuarter(localNow).AddMonths(-3), StartOfQuarter(localNow)),
            PresetKey.ThisYear       => Local(new DateTime(localNow.Year, 1, 1), localNow),
            PresetKey.PreviousYear   => Local(new DateTime(localNow.Year - 1, 1, 1), new DateTime(localNow.Year, 1, 1)),
            _ => new TimeRange(nowUtc.AddDays(-7), nowUtc)
        };
    }

    /// <summary>Próbuje zidentyfikować, że dany zakres (Local) odpowiada konkretnemu presetowi
    /// — pozwala UI ustawić właściwą wartość dropdownu po manualnej edycji dat. Tolerancja 1s.</summary>
    public static PresetKey? Match(DateTime fromLocal, DateTime toLocal, DateTime nowUtc)
    {
        const int toleranceSec = 60;
        foreach (var key in All)
        {
            var r = Resolve(key, nowUtc);
            var fLocal = r.FromUtc.ToLocalTime();
            var tLocal = r.ToUtc.ToLocalTime();
            if (Math.Abs((fLocal - fromLocal).TotalSeconds) < toleranceSec &&
                Math.Abs((tLocal - toLocal).TotalSeconds) < toleranceSec)
                return key;
        }
        return null;
    }

    private static TimeRange Local(DateTime fromLocal, DateTime toLocal)
    {
        var f = DateTime.SpecifyKind(fromLocal, DateTimeKind.Local).ToUniversalTime();
        var t = DateTime.SpecifyKind(toLocal,   DateTimeKind.Local).ToUniversalTime();
        return new TimeRange(f, t);
    }

    private static DateTime StartOfWeek(DateTime localNow)
    {
        // ISO: Mon=0..Sun=6
        var dow = ((int)localNow.DayOfWeek + 6) % 7;
        return localNow.Date.AddDays(-dow);
    }

    private static DateTime StartOfMonth(DateTime localNow)
        => new(localNow.Year, localNow.Month, 1);

    private static DateTime StartOfQuarter(DateTime localNow)
    {
        var firstMonthOfQuarter = ((localNow.Month - 1) / 3) * 3 + 1;
        return new DateTime(localNow.Year, firstMonthOfQuarter, 1);
    }
}
