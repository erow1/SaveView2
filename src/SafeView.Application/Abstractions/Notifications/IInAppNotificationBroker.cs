namespace SafeView.Application.Abstractions.Notifications;

/// <summary>
/// Pojedyncza in-app notyfikacja — publikowana przez <see cref="Detection.IActionHandler"/>
/// (InAppNotification) i konsumowana przez UI (MainLayout subskrybuje event i pokazuje Snackbar).
/// </summary>
public sealed record InAppNotificationMessage(
    string Message,
    InAppNotificationSeverity Severity,
    DateTime PublishedAt);

public enum InAppNotificationSeverity
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// Broker in-app notyfikacji. Singleton, fan-out przez event — każda aktywna sesja Blazor
/// subskrybuje event i pokazuje toast w swoim UI.
/// </summary>
public interface IInAppNotificationBroker
{
    event Action<InAppNotificationMessage>? Published;
    void Publish(InAppNotificationMessage message);
}
