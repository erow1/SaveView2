using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Media;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Cameras.Vendors;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Endpoint do wyświetlania "live-a" z kamery jako seria snapshot-ów:
/// <c>GET /cameras/{id}/snapshot.jpg</c> → zwraca pojedynczą klatkę JPEG.
///
/// Klient (CameraLive.razor) wywołuje go w pętli co ~500ms z cache-busterem,
/// dzięki czemu widzi "obraz na żywo" bez potrzeby HLS/WebRTC.
///
/// Dla kamer DAHUA/Hikvision/Axis — proxy na ich HTTP snapshot CGI
/// (typowo 200-500ms). Dla pozostałych — fallback przez ffmpeg.
/// </summary>
public static class CameraSnapshotEndpoint
{
    public static IEndpointRouteBuilder MapCameraSnapshotEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/cameras/{id}/snapshot.jpg", async (
            string id,
            ICameraRepository cameras,
            ISnapshotService snapshots,
            IHttpClientFactory httpFactory,
            ILogger<Program> log,
            HttpContext http,
            CancellationToken ct) =>
        {
            var camera = await cameras.GetByIdAsync(id, ct);
            if (camera is null) return Results.NotFound();

            // ── Fast path: vendor HTTP snapshot ──────────────────────────────
            var httpUrl = VendorRtspBuilder.BuildHttpSnapshotUrl(camera);
            if (httpUrl is not null)
            {
                var bytes = await TryProxyHttpSnapshotAsync(httpUrl, httpFactory, ct);
                if (bytes is not null)
                {
                    AddNoCacheHeaders(http);
                    return Results.File(bytes, "image/jpeg");
                }
                log.LogDebug("HTTP snapshot proxy failed for {Camera}, falling back to ffmpeg.", camera.Name);
            }

            // ── Fallback: ffmpeg → plik → odczyt ─────────────────────────────
            var result = await snapshots.CaptureAsync(camera, ct);
            if (!result.Success || result.AbsolutePath is null)
                return Results.Problem(result.ErrorMessage ?? "Snapshot failed", statusCode: 502);

            var fileBytes = await File.ReadAllBytesAsync(result.AbsolutePath, ct);
            AddNoCacheHeaders(http);
            return Results.File(fileBytes, "image/jpeg");
        })
        .RequireAuthorization("perm:cameras:view")
        .WithTags("Cameras");

        return endpoints;
    }

    /// <summary>
    /// Pobiera JPEG z vendor HTTP snapshot URL (DAHUA/Hikvision/Axis) używając
    /// HttpClient z Basic/Digest auth ekstraktowanym z URL (format http://user:pass@...).
    /// Zwraca null przy błędzie — caller ma fallback.
    /// </summary>
    private static async Task<byte[]?> TryProxyHttpSnapshotAsync(
        string url, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        try
        {
            var (cleanUrl, creds) = ExtractCredentials(url);

            // Każde wywołanie tworzy nowy HttpClient z dedykowanym handlerem na credentials.
            // Timeout 3s — kamera LAN powinna odpowiedzieć w <500ms.
            var handler = new HttpClientHandler
            {
                Credentials = creds,
                PreAuthenticate = false // pozwól na Digest challenge
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

            using var resp = await client.GetAsync(cleanUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static (string Url, ICredentials? Creds) ExtractCredentials(string url)
    {
        var uri = new Uri(url);
        if (string.IsNullOrEmpty(uri.UserInfo)) return (url, null);

        var parts = uri.UserInfo.Split(':', 2);
        var user = WebUtility.UrlDecode(parts[0]);
        var pass = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "";

        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        return (builder.Uri.ToString(), new NetworkCredential(user, pass));
    }

    private static void AddNoCacheHeaders(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers.Expires = "0";
    }
}
