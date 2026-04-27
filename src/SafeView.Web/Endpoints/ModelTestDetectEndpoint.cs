using SafeView.Application.Abstractions.ML;
using SafeView.Application.Abstractions.Persistence;

namespace SafeView.Web.Endpoints;

/// <summary>
/// <c>POST /api/models/{id}/test-detect</c> — quick-test endpoint dla strony <c>/models</c>.
/// User uploaduje zdjęcie, endpoint puszcza przez aktywny detektor (per MLModel.Backend)
/// i zwraca detekcje + meta. UI renderuje bbox overlay + raw JSON — bez tworzenia kamer,
/// ROI, triggerów. Pozwala sprawdzić "czy ten model faktycznie coś wykrywa na mojej klatce"
/// zanim zainwestujesz w pełną konfigurację.
///
/// <para>Obsługiwane backendy: Onnx (classical YOLO), YoloWorld, YoloE. Dla open-vocab bez
/// dostarczonych promptów używa <see cref="SafeView.Domain.ML.MLModel.Labels"/> jako vocab —
/// tak samo jak pipeline inference fallback.</para>
/// </summary>
public static class ModelTestDetectEndpoint
{
    public static IEndpointRouteBuilder MapModelTestDetectEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/models/{id}/test-detect", async (
                string id,
                HttpRequest request,
                IMLModelRepository modelRepo,
                IDetectorFactory detectorFactory,
                ILogger<ModelTestMarker> log,
                CancellationToken ct) =>
            {
                if (!request.HasFormContentType) return Results.BadRequest("multipart/form-data required");
                var model = await modelRepo.GetByIdAsync(id, ct);
                if (model is null) return Results.NotFound();

                var form = await request.ReadFormAsync(ct);
                if (form.Files.Count == 0) return Results.BadRequest("no image");
                var file = form.Files[0];
                if (file.Length == 0 || file.Length > 20 * 1024 * 1024)
                    return Results.BadRequest("image missing or > 20MB");

                // Zapisujemy do systemowego temp — ad-hoc test, nie pamiętamy między requestami.
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => ".jpg",
                    ".png" => ".png",
                    _ => ".jpg"
                };
                var tempPath = Path.Combine(Path.GetTempPath(), $"sv-test-{Guid.NewGuid():N}{ext}");
                try
                {
                    await using (var src = file.OpenReadStream())
                    await using (var fs = File.Create(tempPath))
                        await src.CopyToAsync(fs, ct);

                    // Opcjonalne custom prompty — text field "prompts" w multipart, JSON array albo CSV.
                    // Gdy podane i model wspiera TextPrompts → używamy ITextPromptDetector.DetectWithPromptsAsync.
                    // Inaczej → standardowy DetectAsync z model.Labels jako fallback (backward-compat).
                    var rawPrompts = form["prompts"].ToString();
                    List<string>? customPrompts = null;
                    if (!string.IsNullOrWhiteSpace(rawPrompts))
                    {
                        try
                        {
                            // Spróbuj JSON array najpierw, fallback do CSV.
                            if (rawPrompts.TrimStart().StartsWith('['))
                            {
                                customPrompts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(rawPrompts)
                                    ?.Where(s => !string.IsNullOrWhiteSpace(s))
                                    .Select(s => s.Trim())
                                    .ToList();
                            }
                            else
                            {
                                customPrompts = rawPrompts
                                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .ToList();
                            }
                        }
                        catch
                        {
                            customPrompts = null;
                        }
                    }

                    // Opcjonalny override confidence threshold per-request — UI suwak. NIE mutujemy
                    // entity (inne wywołania pipeline-u zachowują swój threshold). Klonujemy
                    // tylko jeśli user naprawdę zmienił względem default-u modelu.
                    var rawConf = form["confidence_threshold"].ToString();
                    if (!string.IsNullOrWhiteSpace(rawConf)
                        && double.TryParse(rawConf, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var confOverride)
                        && confOverride >= 0.01 && confOverride <= 0.99
                        && Math.Abs(confOverride - model.ConfidenceThreshold) > 0.001)
                    {
                        model = CloneWithThreshold(model, confOverride);
                    }

                    DetectionResult result;
                    string usedFlow; // info dla UI co faktycznie użyto
                    string? warning = null;
                    try
                    {
                        if (customPrompts is { Count: > 0 }
                            && model.Capabilities.HasFlag(SafeView.Domain.ML.ModelCapabilities.TextPrompts))
                        {
                            var textDetector = detectorFactory.GetTextPromptDetector(model);
                            result = await textDetector.DetectWithPromptsAsync(model, tempPath, customPrompts, ct);
                            usedFlow = "text-prompts";
                        }
                        else
                        {
                            var detector = detectorFactory.GetFor(model);
                            result = await detector.DetectAsync(model, tempPath, ct);
                            usedFlow = "default";
                            // Hint dla usera: ten model nie wspiera dynamic vocab, więc prompty zostały zignorowane.
                            if (customPrompts is { Count: > 0 })
                                warning = "Custom prompts zostały zignorowane — ten model nie ma capability TextPrompts " +
                                          "(closed-set / compiled). Aby użyć text promptów, włącz dynamic export YOLO-World.";
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Test-detect failed for model {Id}", id);
                        return Results.Problem($"inference failed: {ex.Message}");
                    }

                    if (!result.Success)
                    {
                        // Detector zwrócił błąd biznesowy (np. defensive guard "compiled + custom prompts").
                        // Zwracamy 200 ale z błędem w body — UI może to ładnie pokazać zamiast generic 500.
                        return Results.Ok(new
                        {
                            modelId = model.Id,
                            modelName = model.Name,
                            backend = model.Backend.ToString(),
                            capabilities = model.Capabilities.ToString(),
                            error = result.ErrorMessage,
                            usedFlow,
                            usedPrompts = customPrompts ?? model.Labels,
                            detections = Array.Empty<object>()
                        });
                    }

                    return Results.Ok(new
                    {
                        modelId = model.Id,
                        modelName = model.Name,
                        backend = model.Backend.ToString(),
                        capabilities = model.Capabilities.ToString(),
                        sourceWidth = result.SourceWidth,
                        sourceHeight = result.SourceHeight,
                        latencyMs = (int)result.Latency.TotalMilliseconds,
                        confidenceThreshold = model.ConfidenceThreshold,
                        iouThreshold = model.IouThreshold,
                        usedFlow,                                         // "text-prompts" | "default"
                        usedPrompts = customPrompts ?? model.Labels,      // dokładnie co poszło do modelu
                        warning,                                          // null albo komunikat dla usera
                        detections = result.Detections.Select(d => new
                        {
                            classId = d.ClassId,
                            label = d.Label,
                            confidence = d.Confidence,
                            x = d.Box.X,
                            y = d.Box.Y,
                            width = d.Box.Width,
                            height = d.Box.Height
                        })
                    });
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { }
                }
            })
            .DisableAntiforgery()
            .RequireAuthorization("perm:models:view")
            .WithTags("Models");

        return endpoints;
    }

    /// <summary>
    /// Płytki klon MLModel z przesłoniętym <c>ConfidenceThreshold</c>. Detektory czytają threshold
    /// w postprocess, więc per-request override musi pójść przez nową instancję — bez tego nadpisalibyśmy
    /// shared singleton (model w bazie) co rozjebałoby pipeline detekcji innym wywołaniom.
    /// </summary>
    private static SafeView.Domain.ML.MLModel CloneWithThreshold(SafeView.Domain.ML.MLModel src, double threshold)
        => new()
        {
            Id = src.Id,
            Name = src.Name,
            Description = src.Description,
            Backend = src.Backend,
            OnnxRelativePath = src.OnnxRelativePath,
            OnnxAbsolutePath = src.OnnxAbsolutePath,
            InputSize = src.InputSize,
            Labels = src.Labels,
            ConfidenceThreshold = threshold,
            IouThreshold = src.IouThreshold,
            RoboflowModelId = src.RoboflowModelId,
            RoboflowApiKey = src.RoboflowApiKey,
            Enabled = src.Enabled,
            Capabilities = src.Capabilities,
        };

    internal sealed class ModelTestMarker { }
}
