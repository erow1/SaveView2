namespace SafeView.Application.Abstractions.Time;

/// <summary>
/// Abstrakcja czasu — pozwala na deterministyczne testy komponentów czasowych
/// (cooldowny, scheduler, rate-limit) bez `DateTime.UtcNow`.
/// Produkcyjna implementacja: <c>SystemClock</c> w Infrastructure.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
