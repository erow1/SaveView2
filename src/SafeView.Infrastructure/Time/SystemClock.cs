using SafeView.Application.Abstractions.Time;

namespace SafeView.Infrastructure.Time;

/// <summary>Produkcyjny <see cref="IClock"/> — wrapper wokół <c>DateTime.UtcNow</c>.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
