using SafeView.Application.Abstractions.Notifications;

namespace SafeView.Application.Notifications;

public sealed class InAppNotificationBroker : IInAppNotificationBroker
{
    public event Action<InAppNotificationMessage>? Published;

    public void Publish(InAppNotificationMessage message)
        => Published?.Invoke(message);
}
