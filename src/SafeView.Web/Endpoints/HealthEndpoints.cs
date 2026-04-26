using MongoDB.Bson;
using SafeView.Infrastructure.Persistence;
using SafeView.Licensing;

namespace SafeView.Web.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness — czy proces działa.
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
            .AllowAnonymous();

        // Readiness — sprawdza zależności (Mongo, licencja).
        endpoints.MapGet("/health/ready", async (
            IMongoContext mongo,
            ILicenseService license,
            CancellationToken ct) =>
        {
            var checks = new Dictionary<string, object>();
            var ok = true;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                await mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token)
                    .ConfigureAwait(false);
                checks["mongo"] = "ok";
            }
            catch (Exception ex)
            {
                ok = false;
                checks["mongo"] = $"fail: {ex.GetType().Name}";
            }

            var st = license.Status;
            if (st is null)
            {
                checks["license"] = "missing";
            }
            else if (!st.IsValid)
            {
                checks["license"] = "invalid";
                // NIE oznaczamy ready=false — system działa w trybie read-only przy nieprawidłowej licencji.
            }
            else
            {
                checks["license"] = "ok";
            }

            return ok
                ? Results.Ok(new { status = "ready", checks })
                : Results.Json(new { status = "degraded", checks }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        return endpoints;
    }
}
