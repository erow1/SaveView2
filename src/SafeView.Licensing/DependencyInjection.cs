using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SafeView.Licensing.Crypto;

namespace SafeView.Licensing;

public static class DependencyInjection
{
    public static IServiceCollection AddSafeViewLicensing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LicenseOptions>()
            .Bind(configuration.GetSection(LicenseOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<LicenseCryptoService>();
        services.AddSingleton<ILicenseService, LicenseService>();
        services.AddHostedService<LicenseMonitor>();

        return services;
    }
}
