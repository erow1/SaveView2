using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Users;

namespace SafeView.Security.Seed;

/// <summary>
/// Przy starcie aplikacji zapewnia obecność wbudowanych ról.
/// Admin NIE jest tworzony automatycznie — przy pierwszym uruchomieniu
/// (pusta kolekcja users) UI kieruje na /setup gdzie operator podaje własne dane.
/// </summary>
public sealed class IdentitySeeder : IHostedService
{
    private readonly IRoleRepository _roles;
    private readonly ILogger<IdentitySeeder> _log;

    public IdentitySeeder(IRoleRepository roles, ILogger<IdentitySeeder> log)
    {
        _roles = roles;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SeedRolesAsync(cancellationToken).ConfigureAwait(false);
            // Bootstrap admin is NOT auto-created. On first run (users collection empty),
            // the UI redirects to /setup so the operator can create the admin account
            // with their own credentials.
        }
        catch (Exception ex)
        {
            // Nie blokuj startu UI gdy DB niedostępne — log i lecimy dalej
            _log.LogError(ex, "Identity seeding failed (Mongo unreachable?). App will continue; RBAC may be broken.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        foreach (var (name, description, perms) in BuiltInRoles())
        {
            var existing = await _roles.FindByNameAsync(name, ct).ConfigureAwait(false);
            if (existing is null)
            {
                var role = new Role
                {
                    Name = name,
                    Description = description,
                    Permissions = perms.ToList(),
                    IsBuiltIn = true
                };
                await _roles.InsertAsync(role, ct).ConfigureAwait(false);
                _log.LogInformation("Seeded built-in role {Role} ({PermCount} perms)", name, perms.Count);
            }
            else if (existing.IsBuiltIn)
            {
                // Sync permissions in case new permissions are added in a release
                var current = new HashSet<string>(existing.Permissions, StringComparer.Ordinal);
                current.UnionWith(perms);
                if (current.Count != existing.Permissions.Count)
                {
                    existing.Permissions = current.ToList();
                    await _roles.UpdateAsync(existing, ct).ConfigureAwait(false);
                    _log.LogInformation("Updated built-in role {Role} permissions ({PermCount})", name, current.Count);
                }
            }
        }
    }

    private static IEnumerable<(string Name, string Description, IReadOnlyList<string> Permissions)> BuiltInRoles()
    {
        yield return (Role.BuiltInNames.Admin, "Pełny dostęp do systemu", Permission.All);

        yield return (Role.BuiltInNames.Operator, "Operator — obsługa incydentów i kamer",
        [
            Permission.CamerasView, Permission.CamerasEdit,
            Permission.ZonesView, Permission.ZonesEdit,
            Permission.ModelsView,
            Permission.IncidentsView, Permission.IncidentsResolve,
            Permission.ReportsView, Permission.ReportsGenerate,
            Permission.LlmChat
        ]);

        yield return (Role.BuiltInNames.Viewer, "Tylko podgląd",
        [
            Permission.CamerasView,
            Permission.ZonesView,
            Permission.ModelsView,
            Permission.IncidentsView,
            Permission.ReportsView
        ]);
    }
}
