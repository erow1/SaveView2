using SafeView.Domain.ML;

namespace SafeView.Application.Abstractions.ML;

/// <summary>
/// Detektor z kapabilitetem text-prompt open-vocabulary (YOLO-World, YOLOE text mode, itp.).
/// Rozszerza <see cref="IObjectDetector"/> o możliwość podania listy promptów tekstowych
/// przy inferencji — każda detekcja dostaje <c>Label</c> równy jednemu z promptów
/// (najbliższemu semantycznie według text encodera modelu).
///
/// <para><b>Dwa tryby:</b></para>
/// <list type="bullet">
///   <item><b>Dynamic</b> — prompty liczone przez text encoder w locie (wolniejsze, elastyczne).
///     Używane w <c>/admin/vllm-playground</c> typu flow oraz przy testowaniu klasy w edytorze.</item>
///   <item><b>Compiled</b> (Faza 5) — prompty zamrożone w ONNX graph przez re-parametryzację
///     przy eksporcie modelu. Prędkość closed-set, stała lista klas per skompilowany wariant.
///     Reprezentowany jako <see cref="MLModel"/> z <see cref="MLModel.SourceModelId"/> != null.</item>
/// </list>
/// </summary>
public interface ITextPromptDetector : IObjectDetector
{
    /// <summary>Czy session modelu obsługuje dynamic prompty runtime (text encoder w ONNX).
    /// Dla modeli compiled zwraca false — one mają vocab zapieczony.</summary>
    bool SupportsDynamicPrompts(MLModel model);

    /// <summary>
    /// Inferencja z podaną listą promptów tekstowych (typowo po EN dla stabilności CLIP).
    /// Zwracany <c>DetectionResult.Detections[i].Label</c> = jeden z <paramref name="prompts"/>.
    /// </summary>
    Task<DetectionResult> DetectWithPromptsAsync(
        MLModel model,
        string imagePath,
        IReadOnlyList<string> prompts,
        CancellationToken ct = default);
}
