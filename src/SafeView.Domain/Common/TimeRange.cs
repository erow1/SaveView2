namespace SafeView.Domain.Common;

/// <summary>Para znaczników czasu (UTC) opisująca okres "od..do" — używana m.in. do filtrów raportów.</summary>
public readonly record struct TimeRange(DateTime FromUtc, DateTime ToUtc)
{
    public bool IsEmpty => ToUtc <= FromUtc;
    public TimeSpan Duration => ToUtc - FromUtc;
}
