using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.ML;
using SafeView.Domain.Detection;
using SafeView.Domain.ML;
using SafeView.ML.SlicedInference;

namespace SafeView.ML;

/// <summary>
/// Wybiera odpowiedni backend na podstawie <see cref="MLModel.Backend"/> i opcjonalnie
/// trybu inferencji ROI.
/// ONNX: singleton detector (cache sesji wewnątrz).
/// Roboflow: rejestrowany osobno w SafeView.Roboflow, resolved przez DI opcjonalnie.
/// Sliced: decorator <see cref="SlicedDetector"/> nad bazowym ONNX-em.
/// </summary>
public sealed class DetectorFactory : IDetectorFactory
{
    private readonly IServiceProvider _sp;
    public DetectorFactory(IServiceProvider sp) => _sp = sp;

    public IObjectDetector GetFor(MLModel model)
    {
        // Mapping backend → detector. YoloWorld + YoloE implementują IObjectDetector
        // (dziedzicząc przez ITextPromptDetector/IVisualPromptDetector), więc zwracanie
        // ich tu jako IObjectDetector jest legalne. Ich DetectAsync ma fallback do
        // model.Labels gdy brak custom promptów.
        return model.Backend switch
        {
            DetectorBackend.Onnx => _sp.GetRequiredService<OnnxObjectDetector>(),
            DetectorBackend.Roboflow => _sp.GetServices<IObjectDetector>()
                .FirstOrDefault(d => d.Backend == "roboflow")
                ?? throw new InvalidOperationException("Roboflow detector not registered. Add SafeView.Roboflow to DI."),
            DetectorBackend.YoloWorld => _sp.GetRequiredService<SafeView.ML.YoloWorld.OnnxYoloWorldDetector>(),
            DetectorBackend.YoloE => _sp.GetRequiredService<SafeView.ML.YoloE.OnnxYoloEDetector>(),
            DetectorBackend.OwlV2 => _sp.GetRequiredService<SafeView.ML.OwlV2.OnnxOwlV2Detector>(),
            _ => throw new NotSupportedException($"Backend {model.Backend} nie jest wspierany.")
        };
    }

    public IObjectDetector GetFor(MLModel model, RoiInferenceMode mode)
    {
        // Sliced = SAHI decorator; inne tryby (Native/Resize/Adaptive) używają wrapped detector
        // i same decydują o skalowaniu na podstawie rozmiaru cropa.
        if (mode == RoiInferenceMode.Sliced)
            return _sp.GetRequiredService<SlicedDetector>();

        return GetFor(model);
    }

    public ITextPromptDetector GetTextPromptDetector(MLModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Capabilities.HasFlag(ModelCapabilities.TextPrompts))
            throw new InvalidOperationException(
                $"Model '{model.Name}' nie obsługuje text-prompt inferencji (brak flagi Capabilities.TextPrompts).");

        // Wybór konkretnej implementacji per backend. Każdy backend z kapabilitetem TextPrompts
        // musi mieć zarejestrowany singleton implementujący ITextPromptDetector.
        //   YoloWorld   → OnnxYoloWorldDetector  (Faza 3)
        //   YoloE       → OnnxYoloEDetector      (Faza 6, text mode)
        //   <future>    → dodawane tutaj gdy pojawią się nowe backendy
        return model.Backend switch
        {
            DetectorBackend.YoloWorld => ResolveTextDetector("YoloWorld", model),
            DetectorBackend.YoloE => ResolveTextDetector("YoloE", model),
            DetectorBackend.OwlV2 => ResolveTextDetector("OwlV2", model),
            _ => throw new InvalidOperationException(
                $"Backend '{model.Backend}' nie ma zarejestrowanego detektora text-prompt.")
        };
    }

    public IVisualPromptDetector GetVisualPromptDetector(MLModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Capabilities.HasFlag(ModelCapabilities.VisualPrompts))
            throw new InvalidOperationException(
                $"Model '{model.Name}' nie obsługuje visual-prompt inferencji (brak flagi Capabilities.VisualPrompts).");

        // Backend-agnostic resolve. Dziś: YoloE. W przyszłości: OWLv2 / T-Rex / cokolwiek innego
        // pod permissive licencją — dodaje się nowy case bez zmian w pipeline/UI.
        return model.Backend switch
        {
            DetectorBackend.YoloE => ResolveVisualDetector("YoloE", model),
            _ => throw new InvalidOperationException(
                $"Backend '{model.Backend}' nie ma zarejestrowanego detektora visual-prompt.")
        };
    }

    private ITextPromptDetector ResolveTextDetector(string backendName, MLModel model)
    {
        // Wszystkie zarejestrowane implementacje ITextPromptDetector; wybieramy tę która deklaruje
        // ten sam Backend (po stringu w IObjectDetector.Backend). Pozwala dodawać nowe backendy
        // bez dotykania switch-a tej factory — wystarczy rejestracja w DI.
        var matching = _sp.GetServices<ITextPromptDetector>()
            .FirstOrDefault(d => string.Equals(d.Backend, backendName, StringComparison.OrdinalIgnoreCase));
        return matching ?? throw new InvalidOperationException(
            $"Brak zarejestrowanej implementacji ITextPromptDetector dla backend '{backendName}'. " +
            "Zarejestruj w DI przez AddSingleton<ITextPromptDetector, YourDetector>().");
    }

    private IVisualPromptDetector ResolveVisualDetector(string backendName, MLModel model)
    {
        var matching = _sp.GetServices<IVisualPromptDetector>()
            .FirstOrDefault(d => string.Equals(d.Backend, backendName, StringComparison.OrdinalIgnoreCase));
        return matching ?? throw new InvalidOperationException(
            $"Brak zarejestrowanej implementacji IVisualPromptDetector dla backend '{backendName}'. " +
            "Zarejestruj w DI przez AddSingleton<IVisualPromptDetector, YourDetector>().");
    }
}
