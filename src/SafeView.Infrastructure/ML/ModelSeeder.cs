using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.ML;

namespace SafeView.Infrastructure.ML;

/// <summary>
/// Przy starcie aplikacji skanuje <c>runtime/models/{nazwa}/model.onnx</c> i automatycznie
/// rejestruje znalezione modele w bazie (kolekcja <c>ml_models</c>) jeśli jeszcze ich tam nie ma.
///
/// Logika:
///  • Znajdź bundled-models root (patrz <see cref="BundledBinaries.CurrentRid"/> — ten sam pattern)
///  • Skanuj subkatalogi szukając `model.onnx`
///  • Wczytaj towarzyszący `labels.txt` (każda klasa w osobnej linii)
///  • Dla każdego — sprawdź po <c>OnnxAbsolutePath</c> czy istnieje już w bazie
///  • Jeśli NIE → insert z <c>Enabled=false</c> (user aktywuje ręcznie w UI /models)
///
/// Idempotent — można uruchamiać wiele razy, nie duplikuje wpisów.
/// </summary>
public sealed class ModelSeeder : IHostedService
{
    private readonly IMLModelRepository _repo;
    private readonly ILogger<ModelSeeder> _log;

    public ModelSeeder(IMLModelRepository repo, ILogger<ModelSeeder> log)
    {
        _repo = repo;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Migration 2026-04-27: legacy YoloWorld models removed from codebase.
            // Cleanup any stale documents in `ml_models` z Backend=YoloWorld — bez tego
            // /models page rzuca FormatException przy deserializacji enum w MongoDB driver.
            // Po re-add wartości enum jako [Obsolete] deserializacja działa, więc możemy
            // znaleźć i usunąć typowanym query.
            await CleanupLegacyYoloWorldModelsAsync(cancellationToken).ConfigureAwait(false);

            var modelsRoot = FindModelsRoot();
            if (modelsRoot is null)
            {
                _log.LogDebug("ModelSeeder: runtime/models directory not found — skipping (no bundled models).");
                return;
            }

            var existing = await _repo.ListAsync(cancellationToken).ConfigureAwait(false);
            var existingByPath = new HashSet<string>(
                existing.Where(m => !string.IsNullOrEmpty(m.OnnxAbsolutePath))
                        .Select(m => m.OnnxAbsolutePath!),
                StringComparer.OrdinalIgnoreCase);

            var seeded = 0;
            foreach (var subDir in Directory.EnumerateDirectories(modelsRoot))
            {
                var folderName = Path.GetFileName(subDir);
                if (folderName.StartsWith('.')) continue; // .venv, .gitignore, itp.

                var onnxPath = Path.Combine(subDir, "model.onnx");
                if (!File.Exists(onnxPath)) continue;

                if (existingByPath.Contains(onnxPath))
                {
                    _log.LogDebug("ModelSeeder: {Name} już zarejestrowany, pomijam.", folderName);
                    continue;
                }

                var labels = ReadLabels(Path.Combine(subDir, "labels.txt"));
                var description = ReadDescription(Path.Combine(subDir, "README.md"));

                // Rozpoznaj backend po strukturze folderu:
                //   text-encoder.onnx + image-encoder.onnx + tokenizer/ → YOLOE (text + visual)
                //   preprocessor_config.json + tokenizer/              → OWLv2 (Apache 2.0, single-file fused)
                //   default                                            → klasyczny ONNX (YOLOv5/v8/v9 closed-set)
                // YOLO-World usunięty 2026-04-27 (zob. ROADMAP).
                var hasTextEncoder = File.Exists(Path.Combine(subDir, "text-encoder.onnx"));
                var hasImageEncoder = File.Exists(Path.Combine(subDir, "image-encoder.onnx"));
                var hasTokenizer = Directory.Exists(Path.Combine(subDir, "tokenizer"));
                var hasOwlV2Preproc = File.Exists(Path.Combine(subDir, "preprocessor_config.json"));

                DetectorBackend backend;
                ModelCapabilities caps;
                string descPrefix;
                int inputSize = 640;
                if (hasTextEncoder && hasImageEncoder && hasTokenizer)
                {
                    backend = DetectorBackend.YoloE;
                    caps = ModelCapabilities.ClosedSet | ModelCapabilities.TextPrompts | ModelCapabilities.VisualPrompts;
                    descPrefix = "YOLOE (text + visual prompts, AGPL)";
                }
                else if (hasOwlV2Preproc && hasTokenizer)
                {
                    backend = DetectorBackend.OwlV2;
                    caps = ModelCapabilities.TextPrompts; // OWLv2 jest open-vocab only — bez closed-set fallback
                    descPrefix = "OWLv2 (Google, Apache 2.0, ViT-based open-vocab)";
                    // Czytamy InputSize z preprocessor_config.json — różne warianty OWLv2 mają różne
                    // patch sizes (base patch16 → 960, large patch14 → 1008). Niezgodność = ONNX
                    // rzuca shape mismatch w embeddings Add op.
                    inputSize = ReadOwlV2InputSize(Path.Combine(subDir, "preprocessor_config.json")) ?? 960;
                }
                else
                {
                    backend = DetectorBackend.Onnx;
                    caps = ModelCapabilities.ClosedSet;
                    descPrefix = "Bundled model";
                }

                var model = new MLModel
                {
                    Name = folderName,
                    Description = description ?? $"{descPrefix} z runtime/models/{folderName}",
                    Backend = backend,
                    Capabilities = caps,
                    OnnxAbsolutePath = onnxPath,
                    Labels = labels,
                    InputSize = inputSize,
                    ConfidenceThreshold = 0.25,
                    IouThreshold = 0.45,
                    Enabled = false // user musi aktywować
                };

                try
                {
                    await _repo.InsertAsync(model, cancellationToken).ConfigureAwait(false);
                    seeded++;
                    _log.LogInformation("ModelSeeder: zarejestrowano model '{Name}' (backend={Backend}, {Classes} klas, {Size} KB).",
                        folderName, model.Backend, labels.Count, new FileInfo(onnxPath).Length / 1024);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "ModelSeeder: nie udało się zapisać modelu {Name}", folderName);
                }
            }

            if (seeded > 0)
                _log.LogInformation("ModelSeeder: dodano {Count} nowych modeli z runtime/models/. " +
                                    "Aktywuj je w UI → Modele żeby zaczęły działać w detekcji.", seeded);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ModelSeeder failed — bundled modele nie zostaną zarejestrowane.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Migration 2026-04-27: usuwa dokumenty z legacy <c>Backend = "YoloWorld"</c>. Te modele
    /// nie mają już impl-a (OnnxYoloWorldDetector skasowany), trzymanie ich w bazie powoduje
    /// FormatException przy deserializacji enum gdy lista modeli jest ładowana w UI.
    ///
    /// Idempotent — drugie wywołanie nie znajdzie nic do usunięcia. Failure logujemy jako
    /// Warning ale nie przerywamy startup-u (best-effort cleanup).
    /// </summary>
    private async Task CleanupLegacyYoloWorldModelsAsync(CancellationToken ct)
    {
        try
        {
            var all = await _repo.ListAsync(ct).ConfigureAwait(false);
#pragma warning disable CS0618 // Type or member is obsolete — celowo czytamy YoloWorld value
            var legacy = all.Where(m => m.Backend == DetectorBackend.YoloWorld).ToList();
#pragma warning restore CS0618
            foreach (var m in legacy)
            {
                await _repo.DeleteAsync(m.Id, ct).ConfigureAwait(false);
                _log.LogWarning("ModelSeeder: usunięto legacy YOLO-World model '{Name}' (id={Id}). " +
                    "YOLO-World v2 zostało usunięte 2026-04-27 — używaj OWLv2.", m.Name, m.Id);
            }
            if (legacy.Count > 0)
                _log.LogInformation("ModelSeeder: cleanup zakończony, usunięto {Count} legacy YW modeli.", legacy.Count);

            // Migration: dla każdego OWLv2 modelu zsynchronizuj InputSize z preprocessor_config.json
            // (early seed code hardcode-ował 960; OWLv2 large potrzebuje 1008 — niezgodność = ONNX
            // shape mismatch w Add op embeddings vision_model).
            foreach (var m in all.Where(x => x.Backend == DetectorBackend.OwlV2))
            {
                var path = m.OnnxAbsolutePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                var preprocPath = Path.Combine(Path.GetDirectoryName(path)!, "preprocessor_config.json");
                var actualSize = ReadOwlV2InputSize(preprocPath);
                if (actualSize is { } px && px != m.InputSize)
                {
                    var oldSize = m.InputSize;
                    m.InputSize = px;
                    await _repo.UpdateAsync(m, ct).ConfigureAwait(false);
                    _log.LogInformation("ModelSeeder: zaktualizowano InputSize OWLv2 '{Name}' {Old} → {New} " +
                        "(z preprocessor_config.json).", m.Name, oldSize, px);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ModelSeeder: cleanup legacy YW modeli failure (best-effort, kontynuuję).");
        }
    }

    /// <summary>
    /// Znajduje katalog <c>runtime/models</c> idąc w górę od AppContext.BaseDirectory,
    /// analogicznie do <c>BundledBinaries</c> w SafeView.Cameras.
    /// </summary>
    private static string? FindModelsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "runtime", "models");
            if (Directory.Exists(candidate)) return candidate;
        }

        // fallback — sprawdź cwd
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "runtime", "models");
        return Directory.Exists(cwdCandidate) ? cwdCandidate : null;
    }

    /// <summary>
    /// Czyta <c>size.height</c> z <c>preprocessor_config.json</c> OWLv2 (Hugging Face format).
    /// Returns null gdy brak pliku, niepoprawny JSON, albo brak pola size.height — caller fallback.
    /// </summary>
    private static int? ReadOwlV2InputSize(string preprocPath)
    {
        if (!File.Exists(preprocPath)) return null;
        try
        {
            using var stream = File.OpenRead(preprocPath);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("size", out var size)
                && size.TryGetProperty("height", out var h)
                && h.TryGetInt32(out var px) && px > 0)
            {
                return px;
            }
        }
        catch { /* malformed JSON → fallback */ }
        return null;
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

    private static string? ReadDescription(string readmePath)
    {
        if (!File.Exists(readmePath)) return null;
        try
        {
            // Pierwsza nie-pusta linia README.md (bez nagłówka #) jako opis
            foreach (var line in File.ReadLines(readmePath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (trimmed.StartsWith('#'))
                {
                    // przeskocz nagłówek markdown
                    trimmed = trimmed.TrimStart('#').Trim();
                    if (!string.IsNullOrEmpty(trimmed)) return trimmed;
                    continue;
                }
                return trimmed;
            }
        }
        catch { /* ignore */ }
        return null;
    }
}
