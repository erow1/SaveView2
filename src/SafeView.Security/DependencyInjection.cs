using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.Security;
using SafeView.Security.Auth;
using SafeView.Security.Passwords;
using SafeView.Security.Secrets;
using SafeView.Security.Seed;

namespace SafeView.Security;

public static class DependencyInjection
{
    public static IServiceCollection AddSafeViewSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<ISecretCipher, AesGcmSecretCipher>();
        services.AddHostedService<IdentitySeeder>();

        return services;
    }
}
