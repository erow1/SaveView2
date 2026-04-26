using SafeView.Domain.ML;

namespace SafeView.Application.Abstractions.ML;

/// <summary>
/// Opis klasy dla visual-prompt inferencji — semantic label (użyty jako <c>DetectionResult.Label</c>)
/// + lista ścieżek do crop-ów referencyjnych. Krotność: min 1 ref, typowo 3-5.
///
/// <para>Ścieżki absolutne (resolve przez <c>IFileStore</c> leży po stronie caller-a — pipeline).
/// Detector sam może cache'ować embeddingi per-ścieżka żeby nie liczyć ViT po każdej klatce.</para>
/// </summary>
public sealed record VisualPromptClass(
    string ClassId,
    string Label,
    IReadOnlyList<string> ReferenceImagePaths);

/// <summary>
/// Detektor z kapabilitetem visual-prompt open-vocabulary — user pokazuje 1-N crop-ów
/// referencyjnych, model detektuje obiekty wizualnie podobne w target image.
///
/// <para>Implementacje (plug-in): <c>OnnxYoloEDetector</c> (Faza 6, AGPL — pierwsza),
/// <c>OnnxOwlV2Detector</c> (przyszłość, Apache 2.0 — swap target gdy YOLOE wymieni).
/// Interfejs jest backend-agnostic: pipeline i UI nie zależą od konkretnej implementacji.</para>
///
/// <para><b>Swap recipe</b>: (1) nowa wartość <c>DetectorBackend</c>, (2) klasa implementująca
/// ten interfejs, (3) rejestracja w <see cref="IDetectorFactory"/>, (4) nowy MLModel z odpowiednimi
/// <see cref="MLModel.Capabilities"/>. Zero zmian w <c>DetectionClass</c>, <c>Trigger</c>, UI.</para>
/// </summary>
public interface IVisualPromptDetector : IObjectDetector
{
    /// <summary>Czy model (i obecna konfiguracja) faktycznie może robić visual-prompt inferencję.</summary>
    bool SupportsVisualPrompts(MLModel model);

    /// <summary>
    /// Inferencja z podaną listą klas, każda zdefiniowana przez crop-y referencyjne.
    /// Detector liczy visual embeddings z ref-ów (z cache gdy dostępne) i porównuje
    /// z kandydatami w target image. <c>DetectionResult.Label</c> = <see cref="VisualPromptClass.Label"/>.
    /// </summary>
    Task<DetectionResult> DetectWithVisualPromptsAsync(
        MLModel model,
        string imagePath,
        IReadOnlyList<VisualPromptClass> visualClasses,
        CancellationToken ct = default);

    /// <summary>
    /// Sygnalizuje detektorowi że visual refs klasy zostały zmodyfikowane (dodano/usunięto/zmieniono).
    /// Implementacja powinna unieważnić wszelkie cache'owane embeddingi dla <paramref name="classId"/>.
    /// No-op gdy detector nie cache'uje.
    /// </summary>
    void InvalidateClassCache(string classId);
}
