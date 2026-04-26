using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.ML;
using SafeView.Application.Abstractions.Media;
using SafeView.Application.Configuration;
using SafeView.Application.Detection;
using SafeView.Application.Detection.Handlers;

namespace SafeView.ML;

public static class DependencyInjection
{
    public static IServiceCollection AddSafeViewML(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // ML runtime config (GPU acceleration — ticket #2)
        if (configuration is not null)
        {
            services.AddOptions<MLOptions>().Bind(configuration.GetSection(MLOptions.SectionName));
        }
        else
        {
            services.AddOptions<MLOptions>(); // defaults (CPU)
        }

        services.AddSingleton<OnnxObjectDetector>();
        services.AddSingleton<IDetectorFactory, DetectorFactory>();

        // ── YOLO-World (open-vocab, Apache 2.0) — Faza 3 ──────────────────────────
        if (configuration is not null)
        {
            services.AddOptions<SafeView.ML.YoloWorld.YoloWorldOptions>()
                .Bind(configuration.GetSection(SafeView.ML.YoloWorld.YoloWorldOptions.SectionName));
        }
        else
        {
            services.AddOptions<SafeView.ML.YoloWorld.YoloWorldOptions>(); // defaults (InProcessOnnx)
        }

        // CLIP text encoder — rejestracja lazy-safe: factory NIE rzuca gdy ONNX/vocab jeszcze
        // nie pobrane (`scripts/download-models.sh yolo-world-v2-s`). Pass paths nawet gdy
        // pliki brak; ClipTokenizer + OnnxClipTextEncoder same sprawdzają istnienie dopiero
        // przy pierwszym użyciu (Tokenize / GetOrCreateSession). Dzięki temu strony Blazor
        // (np. /admin/prompt-packs) renderują się, a błąd pojawia się przy faktycznej kompilacji.
        services.AddSingleton<SafeView.ML.YoloWorld.ClipTokenizer>(sp =>
        {
            var vocab = FindYoloWorldFile(Path.Combine("tokenizer", "vocab.json"))
                ?? "/yolo-world-v2-s/tokenizer/vocab.json";
            var merges = FindYoloWorldFile(Path.Combine("tokenizer", "merges.txt"))
                ?? "/yolo-world-v2-s/tokenizer/merges.txt";
            return new SafeView.ML.YoloWorld.ClipTokenizer(vocab, merges);
        });

        services.AddSingleton<SafeView.ML.YoloWorld.OnnxClipTextEncoder>(sp =>
        {
            var modelPath = FindYoloWorldFile("text-encoder.onnx")
                ?? "/yolo-world-v2-s/text-encoder.onnx";
            return new SafeView.ML.YoloWorld.OnnxClipTextEncoder(
                modelPath,
                sp.GetRequiredService<SafeView.ML.YoloWorld.ClipTokenizer>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SafeView.ML.YoloWorld.OnnxClipTextEncoder>>());
        });

        services.AddSingleton<SafeView.ML.YoloWorld.ExternalLlmClipTextEncoder>();

        // IClipTextEncoder — wybór strategy przez YoloWorldOptions.EncoderStrategy.
        // Refactor gdy dodajemy trzecią strategy: switch do osobnego ClipTextEncoderSelector.
        services.AddSingleton<IClipTextEncoder>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SafeView.ML.YoloWorld.YoloWorldOptions>>()
                .CurrentValue;
            return opts.EncoderStrategy switch
            {
                SafeView.ML.YoloWorld.ClipEncoderStrategy.ExternalLlm =>
                    sp.GetRequiredService<SafeView.ML.YoloWorld.ExternalLlmClipTextEncoder>(),
                _ => sp.GetRequiredService<SafeView.ML.YoloWorld.OnnxClipTextEncoder>()
            };
        });

        // Detector — rejestrowany pod ITextPromptDetector (factory resolve per backend).
        services.AddSingleton<SafeView.ML.YoloWorld.OnnxYoloWorldDetector>();
        services.AddSingleton<ITextPromptDetector>(sp =>
            sp.GetRequiredService<SafeView.ML.YoloWorld.OnnxYoloWorldDetector>());

        // Faza 5 — Compiled prompt packs (persistent text-embeddings cache)
        services.AddSingleton<SafeView.Application.Abstractions.Detection.IPromptPackCompiler,
                              SafeView.Application.Detection.PromptPackCompiler>();

        // Faza 6 — YOLOE visual-prompt detector (AGPL — swap target dla permissive backend
        // w przyszłości; patrz IVisualPromptDetector docs).
        services.AddSingleton<SafeView.ML.YoloE.OnnxYoloEDetector>();
        services.AddSingleton<ITextPromptDetector>(sp =>
            sp.GetRequiredService<SafeView.ML.YoloE.OnnxYoloEDetector>());
        services.AddSingleton<IVisualPromptDetector>(sp =>
            sp.GetRequiredService<SafeView.ML.YoloE.OnnxYoloEDetector>());

        // SAHI Sliced inference decorator — używany dla ROI z InferenceMode=Sliced/Adaptive (duże)
        services.AddSingleton<SafeView.ML.SlicedInference.SlicedDetector>(sp =>
            new SafeView.ML.SlicedInference.SlicedDetector(
                sp.GetRequiredService<OnnxObjectDetector>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SafeView.ML.SlicedInference.SlicedDetector>>()));

        // ROI cropper — wycinanie ROI z klatki przed inferencją (ImageSharp)
        services.AddSingleton<SafeView.Application.Abstractions.Detection.IRoiCropper,
            SafeView.ML.SlicedInference.ImageSharpRoiCropper>();

        // Detection pipeline Faza 1: zastępuje legacy DetectionFrameObserver.
        // Pipeline rejestrowany jako singleton pod kilkoma interfejsami — IFrameObserver
        // (do wpięcia w CameraFrameSampler) + IDetectionPipeline (do innych wywołań).
        services.AddSingleton<DetectionPipeline>();
        services.AddSingleton<IFrameObserver>(sp => sp.GetRequiredService<DetectionPipeline>());
        services.AddSingleton<IDetectionPipeline>(sp => sp.GetRequiredService<DetectionPipeline>());

        // Trigger evaluator + action dispatcher
        services.AddSingleton<ITriggerEvaluator, TriggerEvaluator>();
        services.AddSingleton<IActionDispatcher, ActionDispatcher>();

        // In-app notification broker — singleton fan-out do aktywnych sesji Blazor
        services.AddSingleton<SafeView.Application.Abstractions.Notifications.IInAppNotificationBroker,
                              SafeView.Application.Notifications.InAppNotificationBroker>();

        // Store ostatnich detekcji per kamera — konsumowany przez /monitor
        services.AddSingleton<SafeView.Application.Abstractions.Detection.IDetectionSnapshotStore,
                              SafeView.Application.Detection.DetectionSnapshotStore>();

        // Handlers akcji — rejestrowane jako kolekcja, ActionDispatcher resolve'uje wszystkie.
        // Dodawanie nowego typu akcji = dodanie kolejnej linii tutaj
        // + nowa klasa implementująca IActionHandler. Bez zmian w dispatcher.
        services.AddSingleton<IActionHandler, LogAlertHandler>();
        services.AddSingleton<IActionHandler, InAppNotificationHandler>();
        // Faza 4 — Email + Webhook:
        services.AddSingleton<IActionHandler, SafeView.Application.Detection.Handlers.EmailActionHandler>();
        services.AddHttpClient(); // wymagane dla WebhookActionHandler (IHttpClientFactory)
        services.AddSingleton<IActionHandler, SafeView.Application.Detection.Handlers.WebhookActionHandler>();

        return services;
    }

    // ── YOLO-World helpers ─────────────────────────────────────────────────

    /// <summary>Szuka katalogu <c>runtime/models/yolo-world-v2-s/</c> idąc w górę od
    /// <see cref="AppContext.BaseDirectory"/>, analogicznie do ModelSeeder.</summary>
    private static string? FindYoloWorldRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "runtime", "models", "yolo-world-v2-s");
            if (Directory.Exists(candidate)) return candidate;
        }
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "runtime", "models", "yolo-world-v2-s");
        return Directory.Exists(cwdCandidate) ? cwdCandidate : null;
    }

    private static string? FindYoloWorldFile(string relativePath)
    {
        var root = FindYoloWorldRoot();
        if (root is null) return null;
        var full = Path.Combine(root, relativePath);
        return File.Exists(full) ? full : null;
    }

}
