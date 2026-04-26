using SafeView.Domain.Common;

namespace SafeView.Domain.Detection;

/// <summary>
/// Typ akcji wykonywanej przez <see cref="DetectionAction"/>.
/// Faza 1: LogAlert, InAppNotification. Faza 4: Email, Webhook, Relay, Sms, …
/// </summary>
public enum ActionType
{
    LogAlert = 0,
    InAppNotification = 1,
    // Faza 4:
    Email = 10,
    Webhook = 11,
    Relay = 12,
    Sms = 13
}

/// <summary>
/// Akcja wykonywana przy wypaleniu Triggera. Polimorficzna — konfiguracja specyficzna dla typu
/// leży w <see cref="Config"/> (string→string map). Każdy typ ma dedykowany <c>IActionHandler</c>
/// w Infrastructure, który rozumie swoje klucze konfiguracji.
///
/// Nazwa <c>DetectionAction</c> (a nie <c>Action</c>) żeby nie kolidować z <see cref="System.Action"/>.
/// </summary>
public sealed class DetectionAction : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ActionType Type { get; set; }

    /// <summary>Konfiguracja specyficzna dla typu — np. dla Email: "to","subject","template".</summary>
    public Dictionary<string, string> Config { get; set; } = [];

    public bool Enabled { get; set; } = true;

    /// <summary>Max wywołań tej akcji na minutę (sliding window). 0 = bez limitu.</summary>
    public int RateLimitPerMinute { get; set; } = 10;
}
