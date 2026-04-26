using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SafeView.Domain.Users;

namespace SafeView.Security.Auth;

public static class SafeViewClaims
{
    public const string Permission = "safeview.permission";
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string DisplayName = "safeview.display_name";
    public const string Language = "safeview.language";
}

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }
    public PermissionRequirement(string permission) => Permission = permission;
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim(SafeViewClaims.Permission, requirement.Permission))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class AuthorizationExtensions
{
    /// <summary>
    /// Rejestruje policy "perm:{code}" dla każdego uprawnienia z <see cref="Permission.All"/>.
    /// Użycie: [Authorize(Policy = "perm:admin:users")].
    /// </summary>
    public static AuthorizationBuilder AddSafeViewPolicies(this AuthorizationBuilder builder)
    {
        foreach (var perm in Permission.All)
        {
            builder.AddPolicy($"perm:{perm}", p => p.AddRequirements(new PermissionRequirement(perm)));
        }
        return builder;
    }

    public static ClaimsPrincipal BuildPrincipal(User user, IEnumerable<string> permissions, string authScheme)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Email, user.Email),
            new(SafeViewClaims.DisplayName, user.DisplayName),
            new(SafeViewClaims.Language, user.PreferredLanguage ?? "pl")
        };
        foreach (var role in user.Roles) claims.Add(new Claim(ClaimTypes.Role, role));
        foreach (var perm in permissions.Distinct(StringComparer.Ordinal))
            claims.Add(new Claim(SafeViewClaims.Permission, perm));
        var identity = new ClaimsIdentity(claims, authScheme, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}
