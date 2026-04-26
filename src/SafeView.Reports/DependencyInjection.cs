using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.Reports;

namespace SafeView.Reports;

public static class DependencyInjection
{
    public static IServiceCollection AddSafeViewReports(this IServiceCollection services)
    {
        services.AddScoped<IReportService, IncidentReportService>();
        return services;
    }
}
