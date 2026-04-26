using SafeView.Domain.Incidents;

namespace SafeView.Application.Abstractions.Notifications;

/// <summary>
/// Wysyła powiadomienia o incydencie do wszystkich skonfigurowanych kanałów (Webhook/SIEM, SMTP, …).
/// Implementacja musi być fail-safe — jeden zepsuty kanał nie blokuje pozostałych.
/// </summary>
public interface INotificationDispatcher
{
    Task DispatchIncidentAsync(Incident incident, CancellationToken ct = default);
}
