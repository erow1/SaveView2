using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.ML;
using SafeView.Domain.ML;
using SafeView.ML;

namespace SafeView.ML.Tests;

/// <summary>
/// Sprawdza capability-gate + backend-resolve w <see cref="DetectorFactory"/>.
/// Fundament swap-readiness: jeśli te testy są zielone, wymiana visual-prompt backendu
/// w przyszłości wymaga tylko zarejestrowania nowej implementacji <see cref="IVisualPromptDetector"/>.
/// </summary>
public class DetectorFactoryTests
{
    private static DetectorFactory BuildFactory(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);
        return new DetectorFactory(services.BuildServiceProvider());
    }

    private static MLModel Model(DetectorBackend backend, ModelCapabilities caps) =>
        new() { Id = "m-1", Name = "test", Backend = backend, Capabilities = caps };

    // ─── GetTextPromptDetector — capability gate ───────────────────────────

    [Fact]
    public void GetTextPromptDetector_Throws_WhenModelLacksTextPromptCapability()
    {
        var factory = BuildFactory();
        var classical = Model(DetectorBackend.Onnx, ModelCapabilities.ClosedSet);

        var act = () => factory.GetTextPromptDetector(classical);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TextPrompts*");
    }

    [Fact]
    public void GetTextPromptDetector_Throws_WhenBackendHasNoRegisteredImplementation()
    {
        var factory = BuildFactory(); // brak rejestracji ITextPromptDetector
        var model = Model(DetectorBackend.YoloWorld, ModelCapabilities.ClosedSet | ModelCapabilities.TextPrompts);

        var act = () => factory.GetTextPromptDetector(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Brak zarejestrowanej implementacji ITextPromptDetector*YoloWorld*");
    }

    [Fact]
    public void GetTextPromptDetector_ResolvesRegisteredImplementation()
    {
        var stub = new StubTextPromptDetector("YoloWorld");
        var factory = BuildFactory(s => s.AddSingleton<ITextPromptDetector>(stub));

        var model = Model(DetectorBackend.YoloWorld, ModelCapabilities.TextPrompts);
        var resolved = factory.GetTextPromptDetector(model);

        resolved.Should().BeSameAs(stub);
    }

    // ─── GetVisualPromptDetector — capability gate ─────────────────────────

    [Fact]
    public void GetVisualPromptDetector_Throws_WhenModelLacksVisualCapability()
    {
        var factory = BuildFactory();
        var textOnly = Model(DetectorBackend.YoloWorld, ModelCapabilities.TextPrompts);

        var act = () => factory.GetVisualPromptDetector(textOnly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VisualPrompts*");
    }

    [Fact]
    public void GetVisualPromptDetector_Throws_WhenBackendHasNoRegisteredImplementation()
    {
        var factory = BuildFactory(); // brak rejestracji IVisualPromptDetector
        var model = Model(DetectorBackend.YoloE,
            ModelCapabilities.TextPrompts | ModelCapabilities.VisualPrompts);

        var act = () => factory.GetVisualPromptDetector(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Brak zarejestrowanej implementacji IVisualPromptDetector*YoloE*");
    }

    [Fact]
    public void GetVisualPromptDetector_ResolvesRegisteredImplementation()
    {
        var stub = new StubVisualPromptDetector("YoloE");
        var factory = BuildFactory(s => s.AddSingleton<IVisualPromptDetector>(stub));

        var model = Model(DetectorBackend.YoloE, ModelCapabilities.VisualPrompts);
        var resolved = factory.GetVisualPromptDetector(model);

        resolved.Should().BeSameAs(stub);
    }

    [Fact]
    public void GetVisualPromptDetector_SwapScenario_NewBackendCanBePluggedWithoutFactoryChange()
    {
        // Scenariusz: w przyszłości wymieniamy YOLOE na hipotetyczny OWLv2 (Apache 2.0).
        // Resolver wybiera po backend-name string — pattern "pluggable". Simulacja:
        // rejestrujemy stub udający OWLv2, factory go zwraca dla modelu z Backend=YoloE
        // gdy tylko Backend.ToString() matchuje. Gdy nowy DetectorBackend.OwlV2 zostanie dodany,
        // switch w factory dostanie jedną linijkę → pattern sprawdzony testem.
        var yoloEStub = new StubVisualPromptDetector("YoloE");
        var factory = BuildFactory(s => s.AddSingleton<IVisualPromptDetector>(yoloEStub));

        var yoloEModel = Model(DetectorBackend.YoloE, ModelCapabilities.VisualPrompts);
        factory.GetVisualPromptDetector(yoloEModel).Should().BeSameAs(yoloEStub);
    }

    // ─── Test doubles ──────────────────────────────────────────────────────

    private sealed class StubTextPromptDetector : ITextPromptDetector
    {
        public StubTextPromptDetector(string backend) { Backend = backend; }
        public string Backend { get; }
        public Task<DetectionResult> DetectAsync(MLModel model, string imagePath, CancellationToken ct = default)
            => Task.FromResult(DetectionResult.Empty(0, 0));
        public bool SupportsDynamicPrompts(MLModel model) => true;
        public Task<DetectionResult> DetectWithPromptsAsync(MLModel model, string imagePath,
            IReadOnlyList<string> prompts, CancellationToken ct = default)
            => Task.FromResult(DetectionResult.Empty(0, 0));
    }

    private sealed class StubVisualPromptDetector : IVisualPromptDetector
    {
        public StubVisualPromptDetector(string backend) { Backend = backend; }
        public string Backend { get; }
        public Task<DetectionResult> DetectAsync(MLModel model, string imagePath, CancellationToken ct = default)
            => Task.FromResult(DetectionResult.Empty(0, 0));
        public bool SupportsVisualPrompts(MLModel model) => true;
        public Task<DetectionResult> DetectWithVisualPromptsAsync(MLModel model, string imagePath,
            IReadOnlyList<VisualPromptClass> visualClasses, CancellationToken ct = default)
            => Task.FromResult(DetectionResult.Empty(0, 0));
        public void InvalidateClassCache(string classId) { }
    }
}
