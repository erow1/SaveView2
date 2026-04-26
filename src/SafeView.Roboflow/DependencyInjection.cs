using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.ML;

namespace SafeView.Roboflow;

public static class DependencyInjection
{
    public static IServiceCollection AddSafeViewRoboflow(this IServiceCollection services)
    {
        services.AddHttpClient<RoboflowObjectDetector>();
        services.AddSingleton<IObjectDetector>(sp => sp.GetRequiredService<RoboflowObjectDetector>());
        return services;
    }
}
