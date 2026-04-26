using SafeView.Domain.ML;

namespace SafeView.Application.Abstractions.ML;

/// <summary>Znormalizowany box w pikselach obrazu źródłowego (top-left + size).</summary>
public sealed record BoundingBox(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

public sealed record Detection(
    int ClassId,
    string Label,
    float Confidence,
    BoundingBox Box);

public sealed record DetectionResult(
    bool Success,
    IReadOnlyList<Detection> Detections,
    int SourceWidth,
    int SourceHeight,
    TimeSpan Latency,
    string? ErrorMessage = null)
{
    public static DetectionResult Empty(int w, int h) =>
        new(true, [], w, h, TimeSpan.Zero);

    public static DetectionResult Failed(string error, int w = 0, int h = 0) =>
        new(false, [], w, h, TimeSpan.Zero, error);
}

/// <summary>
/// Backend detekcji obiektów. Abstrakcja niezależna od ONNX/Roboflow —
/// factory wybiera implementację per-model.
/// </summary>
public interface IObjectDetector
{
    /// <summary>Wysokopoziomowa nazwa backendu (np. "onnx", "roboflow") — do logów i metryk.</summary>
    string Backend { get; }

    /// <summary>
    /// Uruchamia detekcję. <paramref name="imagePath"/> to absolutna ścieżka do pliku JPG/PNG.
    /// </summary>
    Task<DetectionResult> DetectAsync(MLModel model, string imagePath, CancellationToken ct = default);
}

/// <summary>
/// Factory — dla danego modelu zwraca odpowiedni detektor (cache per model).
/// Warming up (ONNX session load) jest wykonywany lazily przy pierwszym użyciu.
///
/// <para><b>Dodawanie nowego backendu</b> (np. zamiana YOLOE na OWLv2):</para>
/// <list type="number">
///   <item>Dodaj wartość do <see cref="DetectorBackend"/></item>
///   <item>Implementuj klasę detektora z odpowiednimi interfejsami
///     (<see cref="IObjectDetector"/> + <see cref="ITextPromptDetector"/> / <see cref="IVisualPromptDetector"/>
///     według kapabilitetów backendu)</item>
///   <item>Zarejestruj singleton w DI</item>
///   <item>Rozszerz switch w <c>DetectorFactory</c> o nowy kind</item>
/// </list>
/// <para>Nic innego (Domain, Trigger, UI) nie wymaga zmian — matching jest capability-driven.</para>
/// </summary>
public interface IDetectorFactory
{
    /// <summary>Zwraca standardowy (non-sliced) detector dla modelu.</summary>
    IObjectDetector GetFor(MLModel model);

    /// <summary>
    /// Zwraca detector dla modelu uwzględniając tryb inferencji ROI.
    /// <c>Sliced</c> zwraca SAHI decorator, inne tryby — standardowy detector.
    /// <c>Native</c>/<c>Resize</c> — wrapped detector robi resize wewnętrznie.
    /// </summary>
    IObjectDetector GetFor(MLModel model, SafeView.Domain.Detection.RoiInferenceMode mode);

    /// <summary>
    /// Zwraca detector z kapabilitetem text-prompt dla podanego modelu.
    /// Rzuca <see cref="InvalidOperationException"/> gdy model nie ma flagi
    /// <see cref="ModelCapabilities.TextPrompts"/> albo backend nie implementuje interfejsu.
    /// </summary>
    ITextPromptDetector GetTextPromptDetector(MLModel model);

    /// <summary>
    /// Zwraca detector z kapabilitetem visual-prompt dla podanego modelu.
    /// Rzuca <see cref="InvalidOperationException"/> gdy model nie ma flagi
    /// <see cref="ModelCapabilities.VisualPrompts"/> albo backend nie implementuje interfejsu.
    /// </summary>
    IVisualPromptDetector GetVisualPromptDetector(MLModel model);
}

/// <summary>
/// Rozszerzenie <see cref="IObjectDetector"/> o batched inference (Faza 4).
/// Pozwala wysłać N obrazów w jednym wywołaniu Run() ONNX Runtime —
/// szczególnie przydatne dla SAHI (N tiles z jednego ROI) gdzie zamiast 8 osobnych
/// inferencji robimy 1× z batch=8 (zwykle 2-4× szybsze na CPU, 4-8× na GPU).
///
/// Wymaga ONNX exportu z <c>dynamic=True</c> — modele bez tego mają fixed batch=1
/// i <see cref="DetectBatchAsync"/> musi zrobić fallback do iteracji sekwencyjnej.
/// </summary>
public interface IBatchObjectDetector : IObjectDetector
{
    /// <summary>Czy session modelu obsługuje batch > 1 (dynamic input dim).</summary>
    bool SupportsBatching(MLModel model);

    /// <summary>
    /// Batched inference — zwraca wyniki per-image w tej samej kolejności co input.
    /// Gdy <see cref="SupportsBatching"/> zwraca false — fallback na sekwencyjne wywołania
    /// (nadal działa, tylko bez przyspieszenia).
    /// </summary>
    Task<DetectionResult[]> DetectBatchAsync(
        MLModel model, IReadOnlyList<string> imagePaths, CancellationToken ct = default);
}
