using SafeView.Domain.Common;

namespace SafeView.Domain.Detection;

/// <summary>
/// Region Of Interest — prostokątny obszar kamery w którym uruchamiane są modele ML.
/// Pozwala:
///  • ograniczyć obszar analizy (oszczędność CPU/GPU)
///  • zachować czułość detekcji dla drobnych obiektów w 4K (przez crop do natywnej rozdzielczości)
///  • przypisać różne modele do różnych części kadru
/// </summary>
public sealed class Roi : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Kamera, do której należy ten ROI.</summary>
    public string CameraId { get; set; } = string.Empty;

    /// <summary>Prostokąt w znormalizowanych koordynatach [0..1] względem klatki kamery.</summary>
    public RoiRectangle Rectangle { get; set; } = new();

    /// <summary>ID modeli ML które mają działać na tym ROI.</summary>
    public List<string> ModelIds { get; set; } = [];

    /// <summary>Tryb inferencji — determinuje jak ROI jest przetwarzany przez model.</summary>
    public RoiInferenceMode InferenceMode { get; set; } = RoiInferenceMode.Adaptive;

    /// <summary>Rozmiar tile przy sliced inference (domyślnie 640 — standardowy YOLO input).</summary>
    public int TileSize { get; set; } = 640;

    /// <summary>Nakładka tiles (0..0.5) przy sliced inference — redukuje cięte obiekty na granicach.</summary>
    public double TileOverlap { get; set; } = 0.2;

    public bool Enabled { get; set; } = true;

    /// <summary>Kolor overlay w UI (hex, np. "#4DA6FF").</summary>
    public string Color { get; set; } = "#4DA6FF";

    // ─── Faza 5: Cascade mode ───────────────────────────────────────────────
    /// <summary>
    /// ID lekkiego modelu "proposer" (opcjonalny). Gdy ustawione, pipeline działa w trybie cascade:
    ///  1. Proposer (np. YOLOv8n) szybko znajduje kandydatów w całym ROI
    ///  2. Każdy kandydat → crop z paddingiem → ciężkie modele z <see cref="ModelIds"/> (confirmers)
    ///
    /// Oszczędza CPU/GPU gdy zdarzenia są rzadkie — confirmer uruchamia się tylko na cropach z detekcją,
    /// zamiast na całym ROI co klatkę. Typowe scenariusze: pożar, intruzi, PPE — ~1% klatek ma zdarzenie.
    ///
    /// Null / empty = normalny flow (każdy model z ModelIds na pełnym ROI).
    /// </summary>
    public string? ProposerModelId { get; set; }

    /// <summary>
    /// Próg confidence dla proposer model (0..1). Tylko kandydaci powyżej tego progu idą do confirmerów.
    /// Niższy próg = więcej kandydatów = więcej confirmer calls (ale większa recall).
    /// </summary>
    public double ProposerConfidenceThreshold { get; set; } = 0.25;

    /// <summary>
    /// Padding wokół bbox kandydata przy cropowaniu dla confirmera (0..1, fraction bbox size).
    /// 0.2 = 20% padding — confirmer widzi kontekst wokół kandydata, lepsza klasyfikacja.
    /// </summary>
    public double CascadeBboxPadding { get; set; } = 0.2;
}

/// <summary>Tryb inferencji per-ROI — decyduje jak radzić sobie z rozdzielczością wycięcia.</summary>
public enum RoiInferenceMode
{
    /// <summary>Crop ≤ rozmiar modelu → inferencja natywna, bez skalowania.</summary>
    Native = 0,

    /// <summary>Crop > rozmiar modelu → resize do input size (szybkie, ale gubi drobne obiekty).</summary>
    Resize = 1,

    /// <summary>SAHI tiling + batch inference (Faza 2) — zachowuje czułość drobnych obiektów.</summary>
    Sliced = 2,

    /// <summary>Automatyczny wybór między Resize a Sliced (Faza 2).</summary>
    Adaptive = 3
}
