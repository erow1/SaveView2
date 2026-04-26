using System.Diagnostics;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.ML;

namespace SafeView.Web.Endpoints;

/// <summary>
/// <c>POST /api/models/download-yolo-world</c> — pobiera bundled YOLO-World v2-s
/// (Apache 2.0) przez <c>scripts/download-models.sh yolo-world-v2-s</c> i rejestruje
/// jako <see cref="MLModel"/> w bazie (enabled=true). Alternatywa dla ręcznego
/// uruchamiania skryptu z terminala — klikalne z UI <c>/admin/prompt-packs</c>.
///
/// <para><b>Bezpieczeństwo</b>: wymaga perm <c>admin:detection-classes</c>. Spawn procesu
/// jest ograniczony do znanego skryptu w repo (pełna ścieżka wyliczona). Żadnego user
/// inputu w argumentach, brak command injection.</para>
///
/// <para><b>Wolny endpoint</b>: pobieranie ~200MB (YOLO-World ONNX + CLIP text encoder) —
/// zajmuje 1-3 min. Timeout serwer-side: 15 minut. Request nie ma streamowania postępu —
/// klient czeka. Rozszerzenie do SSE (streamowanie log-ów) to follow-up ticket.</para>
/// </summary>
public static class DownloadYoloWorldEndpoint
{
    public static IEndpointRouteBuilder MapDownloadYoloWorldEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/models/download-yolo-world", async (
                IMLModelRepository modelRepo,
                ILogger<DownloadYoloWorldMarker> log,
                CancellationToken ct) =>
            {
                var scriptPath = FindScript();
                if (scriptPath is null)
                    return Results.Problem("scripts/download-models.sh nie znaleziono — upewnij się że app działa z katalogu repo.");

                var modelsRoot = FindModelsRoot();
                if (modelsRoot is null)
                    return Results.Problem("runtime/models/ nie znaleziono — brak writable directory.");

                log.LogInformation("YOLO-World download initiated (script={Script})", scriptPath);

                var psi = new ProcessStartInfo
                {
                    FileName = "bash",
                    WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(scriptPath);
                psi.ArgumentList.Add("yolo-world-v2-s");

                using var proc = Process.Start(psi);
                if (proc is null)
                    return Results.Problem("nie udało się uruchomić procesu bash");

                // Czekamy do 15 min na zakończenie. Output trzymamy w pamięci żeby zwrócić
                // przy błędzie.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(15));

                try
                {
                    await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return Results.Problem("timeout 15min — pobieranie nie skończyło się. Spróbuj ręcznie z terminala: ./scripts/download-models.sh yolo-world-v2-s");
                }

                var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
                var stderr = await proc.StandardError.ReadToEndAsync(ct);

                if (proc.ExitCode != 0)
                {
                    log.LogWarning("YOLO-World download failed (exit={Code}): {Err}", proc.ExitCode, stderr);
                    var tail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    if (tail.Length > 500) tail = tail[^500..];
                    return Results.Problem($"download failed (exit {proc.ExitCode}): {tail}");
                }

                var modelPath = Path.Combine(modelsRoot, "yolo-world-v2-s", "model.onnx");
                if (!File.Exists(modelPath))
                    return Results.Problem("skrypt skończył się ale model.onnx nie istnieje — sprawdź logi serwera.");

                // Rejestracja MLModel (albo enable gdy już był)
                var existing = (await modelRepo.ListAsync(ct))
                    .FirstOrDefault(m => string.Equals(m.OnnxAbsolutePath, modelPath, StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    var labels = ReadLabels(Path.Combine(modelsRoot, "yolo-world-v2-s", "labels.txt"));
                    var newModel = new MLModel
                    {
                        Name = "yolo-world-v2-s",
                        Description = "YOLO-World open-vocab (Apache 2.0) — pobrany przez /admin/prompt-packs",
                        Backend = DetectorBackend.YoloWorld,
                        Capabilities = ModelCapabilities.ClosedSet | ModelCapabilities.TextPrompts,
                        OnnxAbsolutePath = modelPath,
                        Labels = labels,
                        InputSize = 640,
                        ConfidenceThreshold = 0.25,
                        IouThreshold = 0.45,
                        Enabled = true // od razu aktywny — user klikający "Pobierz" chce żeby działał
                    };
                    await modelRepo.InsertAsync(newModel, ct);
                    log.LogInformation("YOLO-World model registered (id={Id}, labels={N})", newModel.Id, labels.Count);
                }
                else if (!existing.Enabled)
                {
                    existing.Enabled = true;
                    await modelRepo.UpdateAsync(existing, ct);
                    log.LogInformation("YOLO-World model re-enabled (id={Id})", existing.Id);
                }

                return Results.Ok(new { ok = true, modelPath });
            })
            .RequireAuthorization("perm:admin:detection-classes")
            .WithTags("Models");

        // POST /api/models/upload-yolo-world — multipart upload 4 plików YOLO-World bundle.
        // Fallback gdy auto-download z HF nie działa (401 / firewall / gated repos).
        // User pobiera pliki sam (z dowolnego źródła) i uploaduje. App rozpoznaje po nazwie,
        // układa w runtime/models/yolo-world-v2-s/ i rejestruje MLModel.
        endpoints.MapPost("/api/models/upload-yolo-world", async (
                HttpRequest request,
                IMLModelRepository modelRepo,
                ILogger<DownloadYoloWorldMarker> log,
                CancellationToken ct) =>
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest("multipart/form-data required");

                var modelsRoot = FindModelsRoot();
                if (modelsRoot is null)
                    return Results.Problem("runtime/models/ nie znaleziono.");

                var targetDir = Path.Combine(modelsRoot, "yolo-world-v2-s");
                var tokenizerDir = Path.Combine(targetDir, "tokenizer");
                Directory.CreateDirectory(tokenizerDir);

                var form = await request.ReadFormAsync(ct);
                if (form.Files.Count == 0) return Results.BadRequest("no files uploaded");

                var saved = new List<string>();
                var rejected = new List<string>();
                foreach (var file in form.Files)
                {
                    var rawName = Path.GetFileName(file.FileName).ToLowerInvariant();
                    // Mapowanie typowych nazw plików z HuggingFace na docelowe ścieżki.
                    var dest = rawName switch
                    {
                        "model.onnx" => Path.Combine(targetDir, "model.onnx"),
                        "model_quantized.onnx" => Path.Combine(targetDir, "model.onnx"),
                        "text_model.onnx" => Path.Combine(targetDir, "text-encoder.onnx"),
                        "text-encoder.onnx" => Path.Combine(targetDir, "text-encoder.onnx"),
                        "text_model_quantized.onnx" => Path.Combine(targetDir, "text-encoder.onnx"),
                        "vocab.json" => Path.Combine(tokenizerDir, "vocab.json"),
                        "merges.txt" => Path.Combine(tokenizerDir, "merges.txt"),
                        _ => null
                    };
                    if (dest is null)
                    {
                        log.LogWarning("YOLO-World upload: skipping unknown file '{Name}'", rawName);
                        continue;
                    }

                    // Sanity check — realny ONNX model ma kilka-kilkaset MB, HTML error page ma <1KB.
                    // Jeśli user pobrał plik 29B (redirect/404 page) i wgrywa, odrzucamy z jasnym komunikatem.
                    var minSize = rawName.EndsWith(".onnx", StringComparison.Ordinal) ? 1_000_000L    // 1MB — minimalny real ONNX
                                : rawName == "vocab.json"   ? 100_000L       // 100KB — CLIP vocab ma ~1MB
                                : rawName == "merges.txt"   ? 100_000L       // 100KB — CLIP merges ma ~500KB
                                : 0L;
                    if (file.Length < minSize)
                    {
                        var msg = $"{rawName} ma tylko {file.Length} B — to jest HTML error page z HuggingFace, nie rzeczywisty plik. " +
                                  $"Minimalny realny rozmiar: {minSize / 1024} KB.";
                        log.LogWarning("YOLO-World upload: rejected {Name}: {Reason}", rawName, msg);
                        rejected.Add(msg);
                        continue;
                    }

                    await using var src = file.OpenReadStream();
                    await using var fs = File.Create(dest);
                    await src.CopyToAsync(fs, ct);
                    saved.Add(rawName);
                }

                if (saved.Count == 0 && rejected.Count > 0)
                    return Results.BadRequest(new
                    {
                        ok = false,
                        message = "Wszystkie pliki odrzucone jako zbyt małe — prawdopodobnie pobrałeś HTML error pages, nie rzeczywiste modele. " +
                                  "Sprawdź że source URL faktycznie zwraca plik binarny (kilkanaście+ MB dla ONNX).",
                        rejected
                    });

                if (saved.Count == 0)
                    return Results.BadRequest("no recognized files (expected: model.onnx, text-encoder.onnx, vocab.json, merges.txt)");

                log.LogInformation("YOLO-World upload: saved {Count} files: {List}", saved.Count, string.Join(", ", saved));

                // Sprawdź czy mamy komplet — rejestrujemy tylko gdy wszystkie 4 pliki są na miejscu.
                var modelPath = Path.Combine(targetDir, "model.onnx");
                var textEnc = Path.Combine(targetDir, "text-encoder.onnx");
                var vocab = Path.Combine(tokenizerDir, "vocab.json");
                var merges = Path.Combine(tokenizerDir, "merges.txt");
                var missing = new List<string>();
                if (!File.Exists(modelPath)) missing.Add("model.onnx");
                if (!File.Exists(textEnc)) missing.Add("text-encoder.onnx");
                if (!File.Exists(vocab)) missing.Add("tokenizer/vocab.json");
                if (!File.Exists(merges)) missing.Add("tokenizer/merges.txt");

                if (missing.Count > 0)
                {
                    // Zapisane tyle ile przyszło, ale rejestracja dopiero po komplecie.
                    return Results.Ok(new
                    {
                        ok = false,
                        saved,
                        missing,
                        message = "Wgrano częściowo. Dołącz brakujące pliki: " + string.Join(", ", missing)
                    });
                }

                // Komplet — rejestracja MLModel (jak w download endpoint).
                var existing = (await modelRepo.ListAsync(ct))
                    .FirstOrDefault(m => string.Equals(m.OnnxAbsolutePath, modelPath, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    var labels = ReadLabels(Path.Combine(targetDir, "labels.txt"));
                    if (labels.Count == 0)
                    {
                        labels = ["person", "car", "truck", "bicycle", "motorcycle", "hard hat", "safety vest", "fire", "smoke"];
                    }
                    var newModel = new MLModel
                    {
                        Name = "yolo-world-v2-s",
                        Description = "YOLO-World open-vocab (Apache 2.0) — pliki uploadowane ręcznie",
                        Backend = DetectorBackend.YoloWorld,
                        Capabilities = ModelCapabilities.ClosedSet | ModelCapabilities.TextPrompts,
                        OnnxAbsolutePath = modelPath,
                        Labels = labels,
                        InputSize = 640,
                        ConfidenceThreshold = 0.25,
                        IouThreshold = 0.45,
                        Enabled = true
                    };
                    await modelRepo.InsertAsync(newModel, ct);
                }
                else if (!existing.Enabled)
                {
                    existing.Enabled = true;
                    await modelRepo.UpdateAsync(existing, ct);
                }

                return Results.Ok(new { ok = true, saved });
            })
            .DisableAntiforgery()
            .RequireAuthorization("perm:admin:detection-classes")
            .WithTags("Models");

        // POST /api/models/rescan — rejestruje wszystkie foldery w runtime/models/
        // które nie są jeszcze w DB (duplikuje logikę ModelSeeder, ale on-demand).
        // Używane gdy user pobrał pliki ręcznie (np. gdy auto-download failuje z 401).
        endpoints.MapPost("/api/models/rescan", async (
                IMLModelRepository modelRepo,
                ILogger<DownloadYoloWorldMarker> log,
                CancellationToken ct) =>
            {
                var modelsRoot = FindModelsRoot();
                if (modelsRoot is null)
                    return Results.Problem("runtime/models/ nie znaleziono.");

                var existing = await modelRepo.ListAsync(ct);
                var existingByPath = new HashSet<string>(
                    existing.Where(m => !string.IsNullOrEmpty(m.OnnxAbsolutePath))
                            .Select(m => m.OnnxAbsolutePath!),
                    StringComparer.OrdinalIgnoreCase);

                int added = 0, enabled = 0, cleaned = 0;
                foreach (var subDir in Directory.EnumerateDirectories(modelsRoot))
                {
                    var folderName = Path.GetFileName(subDir);
                    if (folderName.StartsWith('.')) continue;

                    // Auto-cleanup uszkodzonych plików < 1MB (typowo HTML error page z HF
                    // zapisane przez curl bez -f flagą). Inaczej ModelSeeder by je zaakceptował
                    // i potem inference crashowało.
                    var onnxPath = Path.Combine(subDir, "model.onnx");
                    if (File.Exists(onnxPath))
                    {
                        var fi = new FileInfo(onnxPath);
                        if (fi.Length < 1_000_000L)
                        {
                            log.LogWarning("Rescan: usuwam uszkodzony {Path} ({Size}B)", onnxPath, fi.Length);
                            try { File.Delete(onnxPath); cleaned++; } catch { }
                        }
                    }
                    var textEncPath = Path.Combine(subDir, "text-encoder.onnx");
                    if (File.Exists(textEncPath))
                    {
                        var fi = new FileInfo(textEncPath);
                        if (fi.Length < 1_000_000L)
                        {
                            log.LogWarning("Rescan: usuwam uszkodzony {Path} ({Size}B)", textEncPath, fi.Length);
                            try { File.Delete(textEncPath); cleaned++; } catch { }
                        }
                    }

                    if (!File.Exists(onnxPath)) continue;

                    // Rozpoznaj backend po strukturze folderu (identycznie jak ModelSeeder).
                    var hasTextEncoder = File.Exists(Path.Combine(subDir, "text-encoder.onnx"));
                    var hasImageEncoder = File.Exists(Path.Combine(subDir, "image-encoder.onnx"));
                    var hasTokenizer = Directory.Exists(Path.Combine(subDir, "tokenizer"));

                    DetectorBackend backend;
                    ModelCapabilities caps;
                    if (hasTextEncoder && hasImageEncoder && hasTokenizer)
                    {
                        backend = DetectorBackend.YoloE;
                        caps = ModelCapabilities.ClosedSet | ModelCapabilities.TextPrompts | ModelCapabilities.VisualPrompts;
                    }
                    else if (hasTextEncoder && hasTokenizer)
                    {
                        backend = DetectorBackend.YoloWorld;
                        caps = ModelCapabilities.ClosedSet | ModelCapabilities.TextPrompts;
                    }
                    else
                    {
                        backend = DetectorBackend.Onnx;
                        caps = ModelCapabilities.ClosedSet;
                    }

                    if (existingByPath.Contains(onnxPath))
                    {
                        // Już w DB — włącz jeśli disabled (user wgrał pliki do folderu który kiedyś był pusty).
                        var m = existing.First(x => string.Equals(x.OnnxAbsolutePath, onnxPath, StringComparison.OrdinalIgnoreCase));
                        if (!m.Enabled)
                        {
                            m.Enabled = true;
                            await modelRepo.UpdateAsync(m, ct);
                            enabled++;
                        }
                        continue;
                    }

                    var labels = ReadLabels(Path.Combine(subDir, "labels.txt"));
                    var model = new MLModel
                    {
                        Name = folderName,
                        Description = $"Bundled model from runtime/models/{folderName}",
                        Backend = backend,
                        Capabilities = caps,
                        OnnxAbsolutePath = onnxPath,
                        Labels = labels,
                        InputSize = 640,
                        ConfidenceThreshold = 0.25,
                        IouThreshold = 0.45,
                        Enabled = true // user explicite wywołał rescan — chce żeby działało
                    };
                    await modelRepo.InsertAsync(model, ct);
                    added++;
                    log.LogInformation("Rescan: registered {Name} (backend={Backend})", folderName, backend);
                }

                return Results.Ok(new { added, enabled, cleaned });
            })
            .RequireAuthorization("perm:admin:detection-classes")
            .WithTags("Models");

        return endpoints;
    }

    /// <summary>Szuka <c>scripts/download-models.sh</c> w górę od AppContext.BaseDirectory.
    /// Działa dla obu scenariuszy: <c>./start.sh</c> (running z repo root) i
    /// <c>dotnet run</c> (z src/SafeView.Web/bin/.../).</summary>
    private static string? FindScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "download-models.sh");
            if (File.Exists(candidate)) return candidate;
        }
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "download-models.sh");
        return File.Exists(cwdCandidate) ? cwdCandidate : null;
    }

    private static string? FindModelsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "runtime", "models");
            if (Directory.Exists(candidate)) return candidate;
        }
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "runtime", "models");
        return Directory.Exists(cwdCandidate) ? cwdCandidate : null;
    }

    private static List<string> ReadLabels(string labelsPath)
    {
        if (!File.Exists(labelsPath)) return [];
        try
        {
            return File.ReadAllLines(labelsPath)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Typ-znacznik dla ILogger category — żeby logi miały sensowną nazwę.</summary>
    private sealed class DownloadYoloWorldMarker { }
}
