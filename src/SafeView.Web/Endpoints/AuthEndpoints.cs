using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Audit;
using SafeView.Domain.Users;
using SafeView.Security.Auth;
using SafeView.Security.Passwords;

namespace SafeView.Web.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // First-run setup: create the initial admin. Allowed ONLY when users collection is empty.
        endpoints.MapPost("/auth/setup", async (
            HttpContext http,
            [FromForm] string username,
            [FromForm] string email,
            [FromForm] string displayName,
            [FromForm] string password,
            [FromForm] string passwordConfirm,
            IUserRepository users,
            IPasswordHasher hasher,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            // Guard: only allowed on empty DB
            var count = await users.CountAsync(ct).ConfigureAwait(false);
            if (count > 0)
                return Results.Redirect("/login");

            static string? Validate(string username, string email, string displayName, string password, string passwordConfirm)
            {
                if (string.IsNullOrWhiteSpace(username) || username.Length < 3) return "bad_username";
                if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal)) return "bad_email";
                if (string.IsNullOrWhiteSpace(displayName)) return "bad_display_name";
                if (string.IsNullOrWhiteSpace(password) || password.Length < 8) return "weak_password";
                if (!string.Equals(password, passwordConfirm, StringComparison.Ordinal)) return "password_mismatch";
                return null;
            }

            var err = Validate(username, email, displayName, password, passwordConfirm);
            if (err is not null)
                return Results.Redirect($"/setup?error={Uri.EscapeDataString(err)}");

            var admin = new User
            {
                Username = username.Trim(),
                Email = email.Trim(),
                DisplayName = displayName.Trim(),
                PasswordHash = hasher.Hash(password),
                Roles = [Role.BuiltInNames.Admin],
                IsActive = true
            };

            try
            {
                await users.InsertAsync(admin, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.Redirect("/setup?error=insert_failed");
            }

            await audit.LogAsync(new AuditEntry
            {
                UserId = admin.Id,
                Username = admin.Username,
                Action = "user.setup",
                Outcome = AuditOutcome.Success,
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                UserAgent = http.Request.Headers.UserAgent.ToString(),
                Details = "initial admin created"
            }, ct).ConfigureAwait(false);

            return Results.Redirect("/login?setup=ok");
        })
        .AllowAnonymous() // public by design: first-run setup (guarded przez empty-DB check)
        .RequireRateLimiting("login")
        .DisableAntiforgery();

        // Login (POST form) — Blazor Static SSR page posts here
        endpoints.MapPost("/auth/login", async (
            HttpContext http,
            [FromForm] string username,
            [FromForm] string password,
            [FromForm] string? returnUrl,
            IAuthService auth,
            IRoleRepository roles,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var result = await auth.AuthenticateAsync(username, password, ct).ConfigureAwait(false);
            if (!result.Success || result.User is null)
            {
                await audit.LogAsync(new AuditEntry
                {
                    Username = username,
                    Action = "user.login",
                    Outcome = AuditOutcome.Failure,
                    IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = http.Request.Headers.UserAgent.ToString(),
                    Details = result.ErrorCode
                }, ct).ConfigureAwait(false);
                return Results.Redirect($"/login?error={Uri.EscapeDataString(result.ErrorCode ?? "auth_failed")}");
            }

            // Collect permissions across roles
            var perms = new HashSet<string>(StringComparer.Ordinal);
            foreach (var roleName in result.User.Roles)
            {
                var role = await roles.FindByNameAsync(roleName, ct).ConfigureAwait(false);
                if (role is not null)
                    foreach (var p in role.Permissions) perms.Add(p);
            }

            var principal = AuthorizationExtensions.BuildPrincipal(
                result.User, perms, CookieAuthenticationDefaults.AuthenticationScheme);

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) })
                .ConfigureAwait(false);

            await audit.LogAsync(new AuditEntry
            {
                UserId = result.User.Id,
                Username = result.User.Username,
                Action = "user.login",
                Outcome = AuditOutcome.Success,
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                UserAgent = http.Request.Headers.UserAgent.ToString()
            }, ct).ConfigureAwait(false);

            return Results.LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
        })
        .AllowAnonymous() // public by design: login form
        .RequireRateLimiting("login")
        .DisableAntiforgery(); // Blazor static form posts — handled by SameSite=Strict cookie

        endpoints.MapPost("/auth/logout", async (HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            var username = http.User.Identity?.Name;
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(username))
            {
                await audit.LogAsync(new AuditEntry
                {
                    Username = username,
                    Action = "user.logout",
                    Outcome = AuditOutcome.Success,
                    IpAddress = http.Connection.RemoteIpAddress?.ToString()
                }, ct).ConfigureAwait(false);
            }
            return Results.Redirect("/login");
        }).DisableAntiforgery();

        return endpoints;
    }
}
