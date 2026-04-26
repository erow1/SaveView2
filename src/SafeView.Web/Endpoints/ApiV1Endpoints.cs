using Microsoft.AspNetCore.Authorization;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Incidents;
using SafeView.Domain.Users;
using SafeView.Security.Auth;
using SafeView.Web.Auth;

namespace SafeView.Web.Endpoints;

/// <summary>
/// MOD.API — REST v1 dla integratorów. Autoryzacja przez X-API-Key (SHA-256), per-scope policy.
/// </summary>
public static class ApiV1Endpoints
{
    public static IEndpointRouteBuilder MapApiV1Endpoints(this IEndpointRouteBuilder endpoints)
    {
        // Polityki per-scope; każda wymaga schematu ApiKey + odpowiedniego permission.
        AuthorizationPolicy ScopePolicy(string scope) =>
            new AuthorizationPolicyBuilder(ApiKeyAuth.SchemeName)
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(scope))
                .Build();

        var basePolicy = new AuthorizationPolicyBuilder(ApiKeyAuth.SchemeName)
            .RequireAuthenticatedUser()
            .Build();

        var grp = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(basePolicy)
            .RequireRateLimiting("api-key")
            .WithTags("API v1");

        // ── Incidents ────────────────────────────────────────────────────────
        grp.MapGet("/incidents", async (
            DateTime? from,
            DateTime? until,
            int? limit,
            IIncidentRepository incidents,
            CancellationToken ct) =>
        {
            var f = from ?? DateTime.UtcNow.AddDays(-1);
            var u = until ?? DateTime.UtcNow.AddSeconds(1);
            var items = await incidents.ListByDateRangeAsync(f, u, ct);
            var capped = items.Take(Math.Clamp(limit ?? 200, 1, 1000));
            return Results.Ok(capped.Select(i => new
            {
                i.Id,
                i.CameraId,
                i.CameraName,
                i.Category,
                i.Summary,
                Severity = i.Severity.ToString(),
                Status = i.Status.ToString(),
                i.OccurredAt
            }));
        }).RequireAuthorization(ScopePolicy(Permission.ApiIncidentsRead));

        grp.MapGet("/incidents/{id}", async (string id, IIncidentRepository repo, CancellationToken ct) =>
        {
            var i = await repo.GetByIdAsync(id, ct);
            return i is null ? Results.NotFound() : Results.Ok(i);
        }).RequireAuthorization(ScopePolicy(Permission.ApiIncidentsRead));

        // ── Cameras ──────────────────────────────────────────────────────────
        grp.MapGet("/cameras", async (ICameraRepository cameras, CancellationToken ct) =>
        {
            var items = await cameras.ListAsync(ct);
            return Results.Ok(items.Select(c => new
            {
                c.Id, c.Name, c.Location, c.Enabled, c.Tags
            }));
        }).RequireAuthorization(ScopePolicy(Permission.ApiCamerasRead));

        // ── Zones ────────────────────────────────────────────────────────────
        grp.MapGet("/zones", async (string? cameraId, IZoneRepository zones, CancellationToken ct) =>
        {
            var items = string.IsNullOrWhiteSpace(cameraId)
                ? await zones.ListAsync(ct)
                : await zones.ListByCameraAsync(cameraId, ct);
            return Results.Ok(items);
        }).RequireAuthorization(ScopePolicy(Permission.ApiZonesRead));

        // ── Health ───────────────────────────────────────────────────────────
        endpoints.MapGet("/api/v1/ping", (HttpContext ctx) =>
        {
            var who = ctx.User.Identity?.Name ?? "anonymous";
            return Results.Ok(new { ok = true, identity = who, scheme = ctx.User.Identity?.AuthenticationType });
        }).RequireAuthorization(basePolicy);

        return endpoints;
    }
}
