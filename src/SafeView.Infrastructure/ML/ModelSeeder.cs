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
                    inputSize = 960; // OWLv2 wymaga 960×960
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
