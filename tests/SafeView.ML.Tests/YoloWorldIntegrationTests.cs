using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SafeView.Domain.ML;
using SafeView.ML.YoloWorld;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SafeView.ML.Tests;

/// <summary>
/// Smoke test pełnej inferencji YOLO-World na realnym modelu ONNX — wymaga pobranych plików
/// w <c>runtime/models/yolo-world-v2-s/</c>. Testy są **conditionally-skipped** gdy model brak,
/// żeby CI zielone bez bundled ONNX. Dla developera który uruchomił <c>download-models.sh</c>
/// — wykonują się normalnie i weryfikują end-to-end flow (tokenize → CLIP encode → detect).
/// </summary>
public class YoloWorldIntegrationTests
{
    private static string? FindYoloWorldRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "runtime", "models", "yolo-world-v2-s");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool ModelAvailable(out string root, out string modelOnnx, out string textEnc,
        out string vocab, out string merges)
    {
        root = FindYoloWorldRoot() ?? string.Empty;
        modelOnnx = Path.Combine(root, "model.onnx");
        textEnc = Path.Combine(root, "text-encoder.onnx");
        vocab = Path.Combine(root, "tokenizer", "vocab.json");
        merges = Path.Combine(root, "tokenizer", "merges.txt");

        return !string.IsNullOrEmpty(root)
            && File.Exists(modelOnnx) && File.Exists(textEnc)
            && File.Exists(vocab) && File.Exists(merges);
    }

    private static string CreateBlankTestImage()
    {
        // Mały obrazek 640x640 szary — ma "przejść" preprocessing bez realnych wykryć.
        // Głównie sprawdzamy że pipeline się uruchamia end-to-end bez exception-ów.
        var path = Path.Combine(Path.GetTempPath(), $"yolo-world-test-{Guid.NewGuid()}.jpg");
        using var img = new Image<Rgb24>(640, 640, new Rgb24(128, 128, 128));
        img.SaveAsJpeg(path);
        return path;
    }

    [Fact]
    public async Task DetectWithPromptsAsync_EndToEnd_ReturnsSuccessfulResult()
    {
        if (!ModelAvailable(out _, out var modelOnnx, out var textEnc, out var vocab, out var merges))
        {
            // Model nie pobrany — uruchom scripts/download-models.sh yolo-world-v2-s.
            // Test passes automatically (Assert.True(true)) żeby nie blokować CI bez bundled ONNX.
            Assert.True(true, "YOLO-World bundled model nie dostępny — pominięty.");
            return;
        }

        var tokenizer = new ClipTokenizer(vocab, merges);
        using var encoder = new OnnxClipTextEncoder(textEnc, tokenizer, NullLogger<OnnxClipTextEncoder>.Instance);
        using var detector = new OnnxYoloWorldDetector(encoder, NullLogger<OnnxYoloWorldDetector>.Instance);

        var model = new MLModel
        {
            Id = "test-yw",
            Name = "yolo-world-v2-s-test",
            Backend = DetectorBackend.YoloWorld,
            Capabilities = ModelCapabilities.ClosedSet | ModelCapabilities.TextPrompts,
            OnnxAbsolutePath = modelOnnx,
            InputSize = 640,
            ConfidenceThreshold = 0.25,
            IouThreshold = 0.45,
            Enabled = true
        };

        var testImage = CreateBlankTestImage();
        try
        {
            var result = await detector.DetectWithPromptsAsync(
                model, testImage,
                prompts: ["person", "car", "dog"],
                ct: default);

            result.Success.Should().BeTrue("pełny pipeline powinien wykonać się bez błędu");
            result.SourceWidth.Should().Be(640);
            result.SourceHeight.Should().Be(640);
            // Detection count może być 0 na blank image — nie wymuszamy konkretnej liczby,
            // sprawdzamy tylko że wszystkie detekcje mają sensowne dane.
            foreach (var d in result.Detections)
            {
                d.Confidence.Should().BeGreaterThan(0f);
                d.Label.Should().BeOneOf("person", "car", "dog");
            }
        }
        finally
        {
            try { File.Delete(testImage); } catch { }
        }
    }

    [Fact]
    public async Task DetectBatchAsync_EndToEnd_ProcessesMultipleImages()
    {
        if (!ModelAvailable(out _, out var modelOnnx, out var textEnc, out var vocab, out var merges))
        {
            Assert.True(true, "YOLO-World bundled model nie dostępny — pominięty.");
            return;
        }

        var tokenizer = new ClipTokenizer(vocab, merges);
        using var encoder = new OnnxClipTextEncoder(textEnc, tokenizer, NullLogger<OnnxClipTextEncoder>.Instance);
        using var detector = new OnnxYoloWorldDetector(encoder, NullLogger<OnnxYoloWorldDetector>.Instance);

        var model = new MLModel
        {
            Id = "test-yw-batch",
            Name = "yolo-world-v2-s-test-batch",
            Backend = DetectorBackend.YoloWorld,
            Capabilities = ModelCapabilities.ClosedSet | ModelCapabilities.TextPrompts,
            OnnxAbsolutePath = modelOnnx,
            Labels = ["person", "car"],
            InputSize = 640,
            ConfidenceThreshold = 0.25,
            IouThreshold = 0.45,
            Enabled = true
        };

        var img1 = CreateBlankTestImage();
        var img2 = CreateBlankTestImage();
        var img3 = CreateBlankTestImage();
        try
        {
            var results = await detector.DetectBatchAsync(model, [img1, img2, img3]);

            results.Length.Should().Be(3);
            foreach (var r in results)
            {
                r.Success.Should().BeTrue();
                r.SourceWidth.Should().Be(640);
                r.SourceHeight.Should().Be(640);
            }
        }
        finally
        {
            try { File.Delete(img1); } catch { }
            try { File.Delete(img2); } catch { }
            try { File.Delete(img3); } catch { }
        }
    }
}
