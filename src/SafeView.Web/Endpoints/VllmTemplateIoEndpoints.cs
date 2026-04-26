using System.Text.Json;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Vllm;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Import / Export szablonów VLLM jako pliki JSON. Offline-friendly — user może przenieść
/// swoje szablony między instalacjami bez internetu. Built-in szablony są eksportowane
/// ale przy imporcie tracą flagę IsBuiltIn i trafiają jako Custom (bez nadpisania seed-a).
/// </summary>
public static class VllmTemplateIoEndpoints
{
    public static IEndpointRouteBuilder MapVllmTemplateIoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        endpoints.MapGet("/api/vllm/templates/{id}/export", async (
            string id, IPromptTemplateRepository repo, CancellationToken ct) =>
        {
            var t = await repo.GetByIdAsync(id, ct);
            if (t is null) return Results.NotFound();
            var export = ToExport(t);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(export, json);
            var filename = Slugify(t.Name) + ".json";
            return Results.File(bytes, "application/json", filename);
        })
        .RequireAuthorization("perm:llm:configure")
        .WithTags("VLLM");

        endpoints.MapGet("/api/vllm/templates/export-all", async (
            IPromptTemplateRepository repo, CancellationToken ct) =>
        {
            var all = await repo.ListAsync(ct);
            var export = all.Select(ToExport).ToList();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(export, json);
            return Results.File(bytes, "application/json", $"vllm-templates-{DateTime.UtcNow:yyyyMMdd}.json");
        })
        .RequireAuthorization("perm:llm:configure")
        .WithTags("VLLM");

        endpoints.MapPost("/api/vllm/templates/import", async (
            HttpContext http, IPromptTemplateRepository repo, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length == 0) return Results.BadRequest(new { error = "Empty body" });

            List<TemplateExport>? items = null;
            try
            {
                var raw = System.Text.Encoding.UTF8.GetString(ms.ToArray()).TrimStart();
                // Akceptujemy single obiekt albo tablicę
                items = raw.StartsWith('[')
                    ? JsonSerializer.Deserialize<List<TemplateExport>>(raw, json)
                    : new List<TemplateExport> { JsonSerializer.Deserialize<TemplateExport>(raw, json)! };
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
            }
            if (items is null || items.Count == 0)
                return Results.BadRequest(new { error = "No templates in JSON" });

            int inserted = 0;
            foreach (var ex in items)
            {
                if (string.IsNullOrWhiteSpace(ex.Name) ||
                    string.IsNullOrWhiteSpace(ex.SystemPrompt) ||
                    string.IsNullOrWhiteSpace(ex.UserTemplate)) continue;

                await repo.InsertAsync(new PromptTemplate
                {
                    Name = ex.Name.Length > 80 ? ex.Name[..80] : ex.Name,
                    Description = ex.Description ?? string.Empty,
                    Category = string.IsNullOrWhiteSpace(ex.Category) ? "Imported" : ex.Category,
                    SystemPrompt = ex.SystemPrompt,
                    UserTemplate = ex.UserTemplate,
                    SchemaJson = ex.SchemaJson ?? string.Empty,
                    RecommendedMinConfidence = ex.RecommendedMinConfidence is >= 0 and <= 1
                        ? ex.RecommendedMinConfidence.Value : 0.7,
                    DefaultMinSeverity = ex.DefaultMinSeverity ?? ResponseSeverity.Medium,
                    RequireGoodImageQuality = ex.RequireGoodImageQuality ?? true,
                    IsBuiltIn = false, // importowane ZAWSZE jako custom (nawet gdy JSON twierdzi że built-in)
                    BuiltInKey = null
                }, ct);
                inserted++;
            }
            return Results.Ok(new { inserted, total = items.Count });
        })
        .RequireAuthorization("perm:llm:configure")
        .WithTags("VLLM");

        return endpoints;
    }

    private static TemplateExport ToExport(PromptTemplate t) => new(
        Name: t.Name,
        Description: t.Description,
        Category: t.Category,
        SystemPrompt: t.SystemPrompt,
        UserTemplate: t.UserTemplate,
        SchemaJson: t.SchemaJson,
        RecommendedMinConfidence: t.RecommendedMinConfidence,
        DefaultMinSeverity: t.DefaultMinSeverity,
        RequireGoodImageQuality: t.RequireGoodImageQuality);

    private static string Slugify(string s)
    {
        var chars = s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')
            .Select(c => c == ' ' ? '-' : c);
        return new string(chars.ToArray()).Trim('-');
    }

    private sealed record TemplateExport(
        string Name,
        string? Description,
        string? Category,
        string SystemPrompt,
        string UserTemplate,
        string? SchemaJson,
        double? RecommendedMinConfidence,
        ResponseSeverity? DefaultMinSeverity,
        bool? RequireGoodImageQuality);
}
