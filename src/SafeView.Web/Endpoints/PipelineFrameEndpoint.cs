using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Storage;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Serwuje ostatnią klatkę, którą DetectionPipeline przetworzył dla danej kamery.
/// Używane przez /monitor z włączonym trybem "synchronizacja": dzięki temu overlay bboxów
/// zgadza się z obrazem (bo jest to dokładnie klatka na której były liczone detekcje),
/// nie pojawia się efekt "obiekt ucieka bboxowi".
/// </summary>
public static class PipelineFrameEndpoint
{
    public static IEndpointRouteBuilder MapPipelineFrameEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/cameras/{cameraId}/pipeline-frame.jpg", async (
            string cameraId,
            IDetectionSnapshotStore snapshots,
            IFileStore files,
            HttpContext http,
            CancellationToken ct) =>
        {
            var snap = snapshots.GetLatest(cameraId);
            if (snap is null || string.IsNullOrEmpty(snap.FrameRelativePath))
                return Results.NotFound();

            await using var stream = await files.OpenReadAsync(FileKind.Frame, snap.FrameRelativePath, ct);
            if (stream is null) return Results.NotFound();

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);

            http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            http.Response.Headers.Pragma = "no-cache";
            http.Response.Headers.Expires = "0";
            return Results.File(ms.ToArray(), "image/jpeg");
        })
        .RequireAuthorization("perm:cameras:view")
        .WithTags("Monitor");

        return endpoints;
    }
}
