using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Storage;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Serwuje klatkę dowodową przypisaną do <see cref="SafeView.Domain.Incidents.Incident"/>.
/// Bezpieczeństwo: plik odczytywany TYLKO przez <see cref="IFileStore"/> z <see cref="FileKind.Frame"/>,
/// więc nie da się wyciągnąć pliku spoza storage (path-traversal niemożliwy).
/// </summary>
public static class IncidentFrameEndpoint
{
    public static IEndpointRouteBuilder MapIncidentFrameEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/incidents/{id}/frame.jpg", async (
            string id,
            IIncidentRepository incidents,
            IFileStore files,
            CancellationToken ct) =>
        {
            var incident = await incidents.GetByIdAsync(id, ct);
            if (incident is null) return Results.NotFound();
            if (string.IsNullOrEmpty(incident.FrameRelativePath)) return Results.NotFound();

            await using var stream = await files.OpenReadAsync(FileKind.Frame, incident.FrameRelativePath, ct);
            if (stream is null) return Results.NotFound();

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return Results.File(ms.ToArray(), "image/jpeg");
        })
        .RequireAuthorization("perm:incidents:view")
        .WithTags("Incidents");

        return endpoints;
    }
}
