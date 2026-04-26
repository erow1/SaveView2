using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.Notifications;

namespace SafeView.Notifications;

public static class DependencyInjection
{
    public static IServiceCollection AddSafeViewNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName));

        services.AddSingleton<SmtpEmailSender>();
        services.AddHttpClient<WebhookSender>();
        services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();

        // Daily digest (incident summary) — codzienny job.
        services.AddHostedService<DailyDigestJob>();

        return services;
    }
}
