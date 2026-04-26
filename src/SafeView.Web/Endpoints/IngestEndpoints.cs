using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Storage;
using SafeView.Domain.Cameras;
using SafeView.Domain.Detection.Geometry;
using SafeView.Domain.Users;
using SafeView.Security.Auth;
using SafeView.Web.Auth;
using SafeView.Web.Services;
using DetectionResult = SafeView.Application.Abstractions.Detection.DetectionResult;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Push-based ingest dla kamer typu <see cref="CameraTransport.Api"/>:
/// zewnętrzny system wysyła klatki + pre-computed detekcje przez REST.
///
/// Dwa warianty:
///  • <c>POST /api/v1/cameras/{id}/ingest</c> (multipart/form-data) — obraz binarnie + JSON metadata.
///    Primary path dla edge devices.
///  • <c>POST /api/v1/cameras/{id}/ingest-json</c> (application/json) — image base64 lub URL w JSON.
///    Fallback dla cloud webhooków / S3-style integracji.
///
/// Auth: API key z scope <c>api:cameras:write</c>. Optional per-camera pin (<c>Camera.IngestApiKeyId</c>)
/// — gdy ustawiony, tylko ten klucz może pisać do tej kamery (defense-in-depth).
///
/// Format JSON metadata: zob. <see cref="IngestMetadataDto"/>. Bbox w pixel coords (top-left origin),
/// server normalizuje do [0..1] względem <c>frame.width/height</c>. Sentinel ModelId = "external".
/// </summary>
public static class IngestEndpoints
{
    private const string ExternalModelId = "external";
    private const long MaxImageBytes = 20 * 1024 * 1024;        // 20 MB
    private const long MaxJsonBytes = 1 * 1024 * 1024;          // 1 MB metadata
    private const int FrameIdMaxLength = 128;
    private const int LabelMaxLength = 80;

    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        AuthorizationPolicy ScopePolicy(string scope) =>
            new AuthorizationPolicyBuilder(ApiKeyAuth.SchemeName)
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(scope))
                .Build();

        var grp = endpoints.MapGroup("/api/v1/cameras")
            .RequireAuthorization(ScopePolicy(Permission.ApiCamerasWrite))
            .RequireRateLimiting("camera-ingest")
            .WithTags("API v1 — Ingest");

        // ── Multipart (primary) ─────────────────────────────────────────────
        grp.MapPost("/{cameraId}/ingest", async (
            string cameraId,
            HttpRequest req,
            ICameraRepository cameras,
            IFileStore files,
            IDetectionPipeline pipeline,
            IngestIdempotencyStore idempotency,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("IngestEndpoints");
            var sw = Stopwatch.StartNew();

            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "Content-Type must be multipart/form-data" });

            var camera = await cameras.GetByIdAsync(cameraId, ct).ConfigureAwait(false);
            var gateError = ValidateCameraGate(camera, req.HttpContext);
            if (gateError is not null) return gateError;

            IFormCollection form;
            try { form = await req.ReadFormAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid multipart payload", details = ex.Message });
            }

            var frameFile = form.Files["frame"];
            var metadataField = form["metadata"].ToString();
            if (frameFile is null || frameFile.Length == 0)
                return Results.BadRequest(new { error = "Field 'frame' (image file) is required" });
            if (frameFile.Length > MaxImageBytes)
                return Results.BadRequest(new { error = $"Frame too large (max {MaxImageBytes / (1024 * 1024)}MB)" });
            if (string.IsNullOrWhiteSpace(metadataField))
                return Results.BadRequest(new { error = "Field 'metadata' (JSON) is required" });
            if (metadataField.Length > MaxJsonBytes)
                return Results.BadRequest(new { error = $"Metadata too large (max {MaxJsonBytes / 1024}KB)" });

            IngestMetadataDto? meta = TryParseMetadata(metadataField, out var parseError);
            if (meta is null) return Results.BadRequest(new { error = "Invalid metadata JSON", details = parseError });

            var validation = ValidateMetadata(meta);
            if (validation is not null) return validation;

            // Idempotency check
            if (!string.IsNullOrEmpty(meta.Frame.FrameId)
                && !idempotency.TryAdd(camera!.Id, meta.Frame.FrameId))
            {
                sw.Stop();
                return Results.Ok(new IngestResponseDto
                {
                    Status = "already_processed",
                    FrameId = meta.Frame.FrameId,
                    DetectionsCount = 0,
                    ProcessingMs = sw.ElapsedMilliseconds
                });
            }

            // Save frame
            string relPath;
            string absPath;
            try
            {
                using var imgStream = frameFile.OpenReadStream();
                relPath = BuildFrameRelPath(camera!.Id);
                await files.SaveAsync(FileKind.Frame, relPath, imgStream, ct).ConfigureAwait(false);
                absPath = files.ResolveAbsolutePath(FileKind.Frame, relPath);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Ingest: failed to save frame for camera {Camera}", camera!.Name);
                return Results.Problem("Failed to persist frame", statusCode: 500);
            }

            return await DispatchAsync(meta, camera!, absPath, relPath, pipeline, log, sw, ct).ConfigureAwait(false);
        })
        .DisableAntiforgery()
        .WithName("IngestMultipart");

        // ── JSON-only (fallback) ────────────────────────────────────────────
        grp.MapPost("/{cameraId}/ingest-json", async (
            string cameraId,
            HttpRequest req,
            ICameraRepository cameras,
            IFileStore files,
            IDetectionPipeline pipeline,
            IngestIdempotencyStore idempotency,
            IHttpClientFactory? httpFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("IngestEndpoints");
            var sw = Stopwatch.StartNew();

            var camera = await cameras.GetByIdAsync(cameraId, ct).ConfigureAwait(false);
            var gateError = ValidateCameraGate(camera, req.HttpContext);
            if (gateError is not null) return gateError;

            req.EnableBuffering();
            if (req.ContentLength is { } len && len > MaxImageBytes + MaxJsonBytes)
                return Results.BadRequest(new { error = "Payload too large" });

            IngestMetadataDto? meta;
            try
            {
                meta = await JsonSerializer.DeserializeAsync<IngestMetadataDto>(
                    req.Body, JsonOpts, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON body", details = ex.Message });
            }
            if (meta is null) return Results.BadRequest(new { error = "Empty body" });

            var validation = ValidateMetadata(meta);
            if (validation is not null) return validation;
            if (string.IsNullOrEmpty(meta.ImageBase64) && string.IsNullOrEmpty(meta.ImageUrl))
                return Results.BadRequest(new { error = "Either 'image_base64' or 'image_url' required for JSON-only endpoint" });

            if (!string.IsNullOrEmpty(meta.Frame.FrameId)
                && !idempotency.TryAdd(camera!.Id, meta.Frame.FrameId))
            {
                sw.Stop();
                return Results.Ok(new IngestResponseDto
                {
                    Status = "already_processed",
                    FrameId = meta.Frame.FrameId,
                    DetectionsCount = 0,
                    ProcessingMs = sw.ElapsedMilliseconds
                });
            }

            // Resolve image bytes
            byte[] imageBytes;
            try
            {
                if (!string.IsNullOrEmpty(meta.ImageBase64))
                {
                    imageBytes = Convert.FromBase64String(meta.ImageBase64);
                    if (imageBytes.LongLength > MaxImageBytes)
                        return Results.BadRequest(new { error = "Decoded image too large" });
                }
                else
                {
                    imageBytes = await DownloadImageAsync(meta.ImageUrl!, httpFactory, log, ct).ConfigureAwait(false);
                }
            }
            catch (FormatException) { return Results.BadRequest(new { error = "Invalid base64 in image_base64" }); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Ingest-json: image fetch failed for camera {Camera}", camera!.Name);
                return Results.BadRequest(new { error = "Failed to fetch image", details = ex.Message });
            }

            string relPath;
            string absPath;
            try
            {
                using var imgStream = new MemoryStream(imageBytes);
                relPath = BuildFrameRelPath(camera!.Id);
                await files.SaveAsync(FileKind.Frame, relPath, imgStream, ct).ConfigureAwait(false);
                absPath = files.ResolveAbsolutePath(FileKind.Frame, relPath);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Ingest-json: failed to save frame for camera {Camera}", camera!.Name);
                return Results.Problem("Failed to persist frame", statusCode: 500);
            }

            return await DispatchAsync(meta, camera!, absPath, relPath, pipeline, log, sw, ct).ConfigureAwait(false);
        })
        .WithName("IngestJson");

        return endpoints;
    }

    // ── Common helpers ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    private static IngestMetadataDto? TryParseMetadata(string json, out string? error)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<IngestMetadataDto>(json, JsonOpts);
            error = null;
            return dto;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static IResult? ValidateCameraGate(Camera? camera, HttpContext ctx)
    {
        if (camera is null) return Results.NotFound(new { error = "Camera not found" });
        if (camera.Transport != CameraTransport.Api)
            return Results.BadRequest(new { error = $"Camera transport is '{camera.Transport}', not 'Api' — cannot ingest" });
        if (!camera.Enabled)
            return Results.BadRequest(new { error = "Camera is disabled" });

        // Optional per-camera pin: only the bound API key may write
        if (!string.IsNullOrEmpty(camera.IngestApiKeyId))
        {
            var keyId = ctx.User.FindFirst("safeview.api_key_id")?.Value;
            if (!string.Equals(keyId, camera.IngestApiKeyId, StringComparison.Ordinal))
                return Results.Forbid();
        }
        return null;
    }

    private static IResult? ValidateMetadata(IngestMetadataDto meta)
    {
        if (meta.Frame.Width <= 0 || meta.Frame.Height <= 0)
            return Results.BadRequest(new { error = "frame.width and frame.height must be > 0" });

        if (meta.Frame.CapturedAt == default)
            return Results.BadRequest(new { error = "frame.captured_at is required (ISO 8601 UTC)" });

        if (!string.IsNullOrEmpty(meta.Frame.FrameId) && meta.Frame.FrameId.Length > FrameIdMaxLength)
            return Results.BadRequest(new { error = $"frame.frame_id too long (max {FrameIdMaxLength})" });

        // Detections walidacja per-element
        for (var i = 0; i < meta.Detections.Count; i++)
        {
            var d = meta.Detections[i];
            if (string.IsNullOrWhiteSpace(d.Label))
                return Results.BadRequest(new { error = $"detections[{i}].label is required" });
            if (d.Label.Length > LabelMaxLength)
                return Results.BadRequest(new { error = $"detections[{i}].label too long (max {LabelMaxLength})" });
            if (d.Confidence < 0 || d.Confidence > 1)
                return Results.BadRequest(new { error = $"detections[{i}].confidence must be in [0, 1]" });
            if (d.Bbox.Width <= 0 || d.Bbox.Height <= 0)
                return Results.BadRequest(new { error = $"detections[{i}].bbox: width and height must be > 0" });
            if (d.Bbox.X < 0 || d.Bbox.Y < 0
                || d.Bbox.X + d.Bbox.Width > meta.Frame.Width
                || d.Bbox.Y + d.Bbox.Height > meta.Frame.Height)
            {
                return Results.BadRequest(new
                {
                    error = $"detections[{i}].bbox out of frame bounds ({meta.Frame.Width}×{meta.Frame.Height})"
                });
            }
        }
        return null;
    }

    /// <summary>Mapuje DTO → DetectionResult (z normalizacją pixel→[0..1]).</summary>
    private static List<DetectionResult> MapDetections(IngestMetadataDto meta)
    {
        var w = (double)meta.Frame.Width;
        var h = (double)meta.Frame.Height;
        var result = new List<DetectionResult>(meta.Detections.Count);
        foreach (var d in meta.Detections)
        {
            result.Add(new DetectionResult(
                ExternalModelId,
                d.Label,
                d.Confidence,
                new Bbox(d.Bbox.X / w, d.Bbox.Y / h, d.Bbox.Width / w, d.Bbox.Height / h)));
        }
        return result;
    }

    private static string BuildFrameRelPath(string cameraId)
    {
        var now = DateTime.UtcNow;
        return Path.Combine(
            cameraId,
            now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            $"{now:HHmmss}_{Guid.NewGuid():N}.jpg".Replace(':', '-'));
    }

    private static async Task<byte[]> DownloadImageAsync(
        string url, IHttpClientFactory? factory, ILogger log, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("Invalid image_url");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("image_url must be http(s)");

        using var http = factory?.CreateClient() ?? new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(5);
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is { } len && len > MaxImageBytes)
            throw new InvalidOperationException($"Image too large ({len} bytes)");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        if (ms.Length > MaxImageBytes)
            throw new InvalidOperationException($"Image too large ({ms.Length} bytes)");
        return ms.ToArray();
    }

    private static async Task<IResult> DispatchAsync(
        IngestMetadataDto meta,
        Camera camera,
        string absFramePath,
        string relFramePath,
        IDetectionPipeline pipeline,
        ILogger log,
        Stopwatch sw,
        CancellationToken ct)
    {
        try
        {
            var detections = MapDetections(meta);
            await pipeline.ProcessExternalDetectionsAsync(
                camera, absFramePath, relFramePath, detections, meta.Frame.CapturedAt, ct)
                .ConfigureAwait(false);

            sw.Stop();
            return Results.Ok(new IngestResponseDto
            {
                Status = "accepted",
                FrameId = meta.Frame.FrameId,
                DetectionsCount = detections.Count,
                ProcessingMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            log.LogError(ex, "Ingest dispatch failed for camera {Camera}", camera.Name);
            return Results.Problem("Pipeline dispatch failed", statusCode: 500);
        }
    }
}
