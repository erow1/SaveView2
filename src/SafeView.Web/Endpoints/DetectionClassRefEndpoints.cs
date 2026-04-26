using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Storage;
using SafeView.Domain.Detection;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Endpointy do zarządzania visual references klas detekcji (Faza 6).
/// Wzór ścieżki storage: <c>detection-classes/{classId}/refs/{fileName}.jpg</c>.
///
/// <para><b>Bezpieczeństwo path-traversal</b>: zarówno <c>classId</c> jak i <c>refName</c> są
/// walidowane regex-em pasującym do kształtu id-ów Mongo / sluga. Plik jest zawsze
/// dostępny TYLKO przez <see cref="IFileStore"/> z <see cref="FileKind.DetectionClassRef"/>
/// — co gwarantuje że nie da się wyjść poza storage tree.</para>
///
/// <para><b>Format nazwy pliku</b>: <c>{Guid}.{ext}</c> — generowany po stronie serwera żeby
/// uniknąć user-controlled names. User nigdy nie widzi surowej nazwy, tylko relatywną
/// ścieżkę zapisaną w <see cref="VisualReference.RelativePath"/>.</para>
/// </summary>
public static partial class DetectionClassRefEndpoints
{
    // Restrykcyjny regex: Mongo ObjectId (24 hex) albo UUID-style. Blokuje ścieżki, kropki, slashe.
    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    // refName = {Guid}.{ext} — sprawdzamy dokładnie ten format.
    [GeneratedRegex(@"^[a-f0-9-]{32,40}\.(jpg|jpeg|png)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RefNameRegex();

    public static IEndpointRouteBuilder MapDetectionClassRefEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET — serwuje plik referencyjny (thumbnail w UI).
        endpoints.MapGet("/api/detection-classes/{classId}/refs/{refName}", async (
                string classId,
                string refName,
                IFileStore files,
                CancellationToken ct) =>
            {
                if (!IdentifierRegex().IsMatch(classId)) return Results.BadRequest("invalid classId");
                if (!RefNameRegex().IsMatch(refName)) return Results.BadRequest("invalid refName");

                var relativePath = Path.Combine(classId, "refs", refName);
                await using var stream = await files.OpenReadAsync(FileKind.DetectionClassRef, relativePath, ct);
                if (stream is null) return Results.NotFound();

                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                var contentType = refName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                return Results.File(ms.ToArray(), contentType);
            })
            .RequireAuthorization("perm:admin:detection-classes")
            .WithTags("DetectionClasses");

        // POST — upload nowego crop-a + append do VisualReferences klasy.
        endpoints.MapPost("/api/detection-classes/{classId}/refs", async (
                string classId,
                HttpRequest request,
                IDetectionClassRepository classes,
                IFileStore files,
                CancellationToken ct) =>
            {
                if (!IdentifierRegex().IsMatch(classId)) return Results.BadRequest("invalid classId");

                var klass = await classes.GetByIdAsync(classId, ct);
                if (klass is null) return Results.NotFound();
                if (klass.IsBuiltIn) return Results.BadRequest("cannot modify built-in class");

                if (!request.HasFormContentType) return Results.BadRequest("multipart/form-data required");
                var form = await request.ReadFormAsync(ct);
                if (form.Files.Count == 0) return Results.BadRequest("no file");
                var file = form.Files[0];
                if (file.Length == 0) return Results.BadRequest("empty file");
                if (file.Length > 5 * 1024 * 1024) return Results.BadRequest("file too large (max 5MB)");

                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png")
                    return Results.BadRequest("only jpg/jpeg/png allowed");
                var normalizedExt = ext == ".jpeg" ? ".jpg" : ext;

                var fileName = $"{Guid.NewGuid():N}{normalizedExt}";
                var relativePath = Path.Combine(classId, "refs", fileName);

                await using (var source = file.OpenReadStream())
                    await files.SaveAsync(FileKind.DetectionClassRef, relativePath, source, ct);

                klass.VisualReferences.Add(new VisualReference
                {
                    RelativePath = relativePath,
                    UploadedAt = DateTime.UtcNow
                });
                await classes.UpdateAsync(klass, ct);

                return Results.Ok(new { fileName, relativePath, count = klass.VisualReferences.Count });
            })
            .DisableAntiforgery() // multipart upload bez tokenu CSRF — zabezpieczenie przez perm policy + cookie auth
            .RequireAuthorization("perm:admin:detection-classes")
            .WithTags("DetectionClasses");

        // DELETE — usuwa plik + VisualReference z klasy.
        endpoints.MapDelete("/api/detection-classes/{classId}/refs/{refName}", async (
                string classId,
                string refName,
                IDetectionClassRepository classes,
                IFileStore files,
                CancellationToken ct) =>
            {
                if (!IdentifierRegex().IsMatch(classId)) return Results.BadRequest("invalid classId");
                if (!RefNameRegex().IsMatch(refName)) return Results.BadRequest("invalid refName");

                var klass = await classes.GetByIdAsync(classId, ct);
                if (klass is null) return Results.NotFound();
                if (klass.IsBuiltIn) return Results.BadRequest("cannot modify built-in class");

                var relativePath = Path.Combine(classId, "refs", refName);
                var removed = klass.VisualReferences.RemoveAll(r =>
                    string.Equals(r.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
                if (removed == 0) return Results.NotFound();

                await files.DeleteAsync(FileKind.DetectionClassRef, relativePath, ct);
                await classes.UpdateAsync(klass, ct);
                return Results.Ok(new { count = klass.VisualReferences.Count });
            })
            .RequireAuthorization("perm:admin:detection-classes")
            .WithTags("DetectionClasses");

        return endpoints;
    }
}
