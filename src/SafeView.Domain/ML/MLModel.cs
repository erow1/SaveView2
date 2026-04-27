using SafeView.Domain.Common;

namespace SafeView.Domain.ML;

public enum DetectorBackend
{
    Onnx = 0,
    Roboflow = 1,

    // YoloWorld = 2 — usunięty 2026-04-27 (model dawał false positives, prompty matchowały
    // wizualnie podobne fragmenty zamiast prawdziwych obiektów). Zastąpiony przez OWLv2 (= 4).
    // Stara wartość zarezerwowana dla backward-compat z dokumentami w bazie.

    /// <summary>YOLOE (Ultralytics, text + visual prompts) — AGPL/Enterprise License.
    /// <para><b>Swap note:</b> ten backend wypełnia rolę "visual-prompt slot". Jeśli w przyszłości
    /// zamienimy go na alternatywę pod permissive licencją (np. OWLv2, T-Rex gdy zmieni licencję,
    /// inny visual-prompt model) — dodajemy nowy wariant tej enumy, implementujemy
    /// <c>IVisualPromptDetector</c>, rejestrujemy w <c>DetectorFactory</c>. Nic w Domain,
    /// <c>DetectionClass</c>, <c>Trigger</c>, ani UI nie wymaga zmian — all matching jest
    /// capability-driven przez <c>ModelCapabilities</c>.</para></summary>
    YoloE = 3,

    /// <summary>OWLv2 (Google, Apache 2.0) — open-vocabulary text-prompt detector based on
    /// Vision Transformer + CLIP-style text encoder, fused into single ONNX. Lepsza detekcja
    /// rzadkich klas i części obiektów (face, pants, ...) vs YOLO-World v2-s.
    /// 960×960 input, 3600 anchors, output: logits → sigmoid → scores, pred_boxes cxywh-norm.
    /// 3 inputs (pixel_values, input_ids, attention_mask), 4 outputs (używamy logits + pred_boxes).
    /// Pobierany z <c>onnx-community/owlv2-base-patch16-ensemble-ONNX</c>.</summary>
    OwlV2 = 4
}

/// <summary>
/// Kapabilitety detektora — flagi informujące pipeline jakie tryby inferencji obsługuje model.
/// Klasyczne YOLO ma tylko <see cref="ClosedSet"/>. OWLv2 ma <see cref="TextPrompts"/>.
/// YOLOE ma wszystkie trzy. Flaga pozwala jednemu modelowi wchodzić w różne role per ROI/Trigger
/// bez duplikacji wpisu w <c>ml_models</c>.
/// </summary>
[Flags]
public enum ModelCapabilities
{
    None = 0,
    /// <summary>Fixed vocabulary — <see cref="MLModel.Labels"/> określa klasy zapieczone w wagach.</summary>
    ClosedSet = 1,
    /// <summary>Text-prompt open-vocabulary (OWLv2, YOLOE text mode).</summary>
    TextPrompts = 2,
    /// <summary>Visual-prompt open-vocabulary (YOLOE — reference crops).</summary>
    VisualPrompts = 4
}

/// <summary>
/// Model detekcji obiektów — .onnx na dysku lub endpoint Roboflow.
/// </summary>
public sealed class MLModel : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DetectorBackend Backend { get; set; } = DetectorBackend.Onnx;

    /// <summary>Ścieżka RELATYWNA (wg IFileStore, FileKind.Models) do pliku .onnx. Wymagane dla backend=Onnx gdy AbsolutePath null.</summary>
    public string? OnnxRelativePath { get; set; }

    /// <summary>
    /// Ścieżka ABSOLUTNA do pliku .onnx. Używana dla bundled modeli z <c>runtime/models/</c>.
    /// Gdy ustawione — ma pierwszeństwo przed <see cref="OnnxRelativePath"/>.
    /// </summary>
    public string? OnnxAbsolutePath { get; set; }

    /// <summary>Wejście sieci, kwadrat, typowo 640.</summary>
    public int InputSize { get; set; } = 640;

    /// <summary>Lista etykiet — indeks = classId. Długość musi pasować do liczby klas modelu.</summary>
    public List<string> Labels { get; set; } = [];

    public double ConfidenceThreshold { get; set; } = 0.25;
    public double IouThreshold { get; set; } = 0.45;

    // ─── Roboflow ──────────────────────────────────────────────────────────────
    /// <summary>np. "ppe-safety/3" (workspace/version). Wymagane dla backend=Roboflow.</summary>
    public string? RoboflowModelId { get; set; }
    public string? RoboflowApiKey { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Kapabilitety detektora — flagi wskazujące jakie tryby inferencji obsługuje ten model.
    /// Domyślnie <see cref="ModelCapabilities.ClosedSet"/> (klasyczne YOLO). Dla OWLv2
    /// ustaw <c>TextPrompts</c>; dla YOLOE wszystkie trzy flagi.
    /// </summary>
    public ModelCapabilities Capabilities { get; set; } = ModelCapabilities.ClosedSet;
}
