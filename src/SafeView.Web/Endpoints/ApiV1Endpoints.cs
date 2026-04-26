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
        })
        .RequireAuthorization(ScopePolicy(Permission.ApiIncidentsRead))
        .WithTags("Incidents")
        .WithSummary("Lista incydentów w przedziale czasu")
        .WithDescription("Filtry: `from` (UTC, default: -24h), `until` (UTC, default: now), `limit` (1..1000, default: 200). Wymaga scope `api:incidents:read`.")
        .Produces(200)
        .ProducesProblem(401);

        grp.MapGet("/incidents/{id}", async (string id, IIncidentRepository repo, CancellationToken ct) =>
        {
            var i = await repo.GetByIdAsync(id, ct);
            return i is null ? Results.NotFound() : Results.Ok(i);
        })
        .RequireAuthorization(ScopePolicy(Permission.ApiIncidentsRead))
        .WithTags("Incidents")
        .WithSummary("Pełen szczegół incydentu")
        .WithDescription("Zwraca pełną encję `Incident` (z detekcjami, trigger info, klatka path).")
        .Produces<Incident>(200)
        .Produces(404)
        .ProducesProblem(401);

        // ── Cameras ──────────────────────────────────────────────────────────
        grp.MapGet("/cameras", async (ICameraRepository cameras, CancellationToken ct) =>
        {
            var items = await cameras.ListAsync(ct);
            return Results.Ok(items.Select(c => new
            {
                c.Id, c.Name, c.Location, c.Enabled, c.Tags
            }));
        })
        .RequireAuthorization(ScopePolicy(Permission.ApiCamerasRead))
        .WithTags("Cameras")
        .WithSummary("Lista kamer")
        .WithDescription("Zwraca podstawowe metadane wszystkich kamer (Id/Name/Location/Enabled/Tags). Wymaga scope `api:cameras:read`.")
        .Produces(200)
        .ProducesProblem(401);

        // ── Zones ────────────────────────────────────────────────────────────
        grp.MapGet("/zones", async (string? cameraId, IZoneRepository zones, CancellationToken ct) =>
        {
            var items = string.IsNullOrWhiteSpace(cameraId)
                ? await zones.ListAsync(ct)
                : await zones.ListByCameraAsync(cameraId, ct);
            return Results.Ok(items);
        })
        .RequireAuthorization(ScopePolicy(Permission.ApiZonesRead))
        .WithTags("Zones")
        .WithSummary("Lista stref (opcjonalnie filtrowana po kamerze)")
        .WithDescription("Query param: `cameraId` (opcjonalny). Bez filtra → wszystkie strefy. Wymaga scope `api:zones:read`.")
        .Produces(200)
        .ProducesProblem(401);

        // ── Health ───────────────────────────────────────────────────────────
        endpoints.MapGet("/api/v1/ping", (HttpContext ctx) =>
        {
            var who = ctx.User.Identity?.Name ?? "anonymous";
            return Results.Ok(new { ok = true, identity = who, scheme = ctx.User.Identity?.AuthenticationType });
        })
        .RequireAuthorization(basePolicy)
        .WithTags("Health")
        .WithSummary("Sanity check — sprawdza że API key działa")
        .WithDescription("Zwraca `{ok, identity, scheme}`. Najprostszy test integracyjny dla nowego klucza.")
        .Produces(200)
        .ProducesProblem(401);

        return endpoints;
    }
}
