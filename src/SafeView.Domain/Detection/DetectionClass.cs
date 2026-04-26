using SafeView.Domain.Common;
using SafeView.Domain.ML;

namespace SafeView.Domain.Detection;

/// <summary>
/// Rodzaj klasy detekcji — determinuje jak pipeline mapuje <c>DetectionClass</c> na fizyczny
/// detektor i jak matchuje detekcje w <see cref="TriggerCondition"/>.
/// </summary>
public enum DetectionClassKind
{
    /// <summary>Binding do klasycznego closed-set modelu — <see cref="DetectionClass.ClosedSetModelId"/> +
    /// <see cref="DetectionClass.ClosedSetLabel"/> wskazują jedną konkretną klasę wytrenowaną w modelu.</summary>
    ClosedSetBinding = 0,

    /// <summary>Text-prompt open-vocab — <see cref="DetectionClass.TextPrompt"/> jest zapytaniem
    /// przekazywanym do detektora obsługującego <see cref="ModelCapabilities.TextPrompts"/>.</summary>
    Text = 1,

    /// <summary>Visual-prompt open-vocab — <see cref="DetectionClass.VisualReferences"/> to
    /// crop-y referencyjne dla detektora obsługującego <see cref="ModelCapabilities.VisualPrompts"/>.</summary>
    Visual = 2,

    /// <summary>Hybrid — oba prompty jednocześnie (YOLOE). Detektor łączy embeddings.</summary>
    TextAndVisual = 3
}

/// <summary>
/// Pojedyncza referencja wizualna dla visual-prompt open-vocab detektora. Crop przechowywany
/// w <c>storage/detection-classes/{classId}/refs/</c> przez IFileStore.
///
/// <para><b>Design note — swap-readiness:</b> encja nie trzyma embeddingów visualnych
/// (vector cache), bo te są specyficzne per implementacja detektora (YOLOE CLIP-image ≠
/// OWLv2 SigLIP ≠ dowolny inny backend visual). Cache embeddingów żyje w warstwie detektora
/// (Faza 6: <c>IVisualEmbeddingCache</c>), keyed po (backendId, visualReferenceId).
/// Wymiana visual-prompt backendu = unieważnienie cache, bez zmian w domain ani w user data.</para>
/// </summary>
public sealed class VisualReference
{
    /// <summary>Relatywna ścieżka crop-a (wg IFileStore).</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Opcjonalne źródło — ID klatki/incidentu z którego crop został wycięty.</summary>
    public string? SourceFrameId { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// <para>Klasa detekcji jako pierwszoklasowa encja domeny — abstrahuje "co wykrywamy"
/// od "jakiego modelu używamy". Ten sam <c>DetectionClass</c> może być obsługiwany przez:
/// classical YOLO (gdy <see cref="Kind"/>=<see cref="DetectionClassKind.ClosedSetBinding"/>),
/// YOLO-World (<see cref="DetectionClassKind.Text"/>), lub YOLOE (każdy kind).</para>
///
/// <para>Built-in klasy ładowane przez <c>DetectionClassSeeder</c> — zawsze dostępne,
/// read-only w UI. User duplikuje built-in jako bazę własnej klasy. Pattern 1:1 z <c>PromptTemplate</c>.</para>
///
/// <para>Trigger odwołuje się przez <see cref="TriggerCondition.DetectionClassId"/> (opcjonalnie,
/// backward-compat ze starymi <c>Labels</c>/<c>ModelId</c> stringami).</para>
/// </summary>
public sealed class DetectionClass : Entity
{
    /// <summary>Wyświetlana nazwa (np. "Pracownik bez kasku"). Unique-ish, nieweryfikowane twardo.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Krótki opis scenariusza — pokazywany przy wyborze klasy w UI.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Kategoria grupująca klasy w UI — "PPE", "Pożar", "Ruch", "Maszyny", itp.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Rodzaj klasy — determinuje którego detektora użyć.</summary>
    public DetectionClassKind Kind { get; set; } = DetectionClassKind.ClosedSetBinding;

    // ─── Text-prompt (YOLO-World / YOLOE text) ─────────────────────────────

    /// <summary>Prompt tekstowy po angielsku (stabilniej dla modeli CLIP-based). Wymagane dla
    /// <see cref="DetectionClassKind.Text"/> i <see cref="DetectionClassKind.TextAndVisual"/>.
    /// Np. <c>"construction worker wearing hard hat"</c>.</summary>
    public string? TextPrompt { get; set; }

    /// <summary>Wersja polska prompta — do wyświetlania w UI. Nie używana w inferencji.</summary>
    public string? TextPromptPl { get; set; }

    // ─── Visual-prompt (YOLOE) ─────────────────────────────────────────────

    /// <summary>Crop-y referencyjne dla visual-prompt. Wymagane min 1 dla
    /// <see cref="DetectionClassKind.Visual"/> / <see cref="DetectionClassKind.TextAndVisual"/>.</summary>
    public List<VisualReference> VisualReferences { get; set; } = [];

    // ─── Closed-set binding (klasyczny YOLO) ────────────────────────────────

    /// <summary>ID modelu dla closed-set binding. Wymagane dla
    /// <see cref="DetectionClassKind.ClosedSetBinding"/>.</summary>
    public string? ClosedSetModelId { get; set; }

    /// <summary>Label w modelu docelowym — np. <c>"person"</c>. Wymagany dla
    /// <see cref="DetectionClassKind.ClosedSetBinding"/>.</summary>
    public string? ClosedSetLabel { get; set; }

    // ─── Wspólne ────────────────────────────────────────────────────────────

    /// <summary>Rekomendowany minimalny próg confidence (0..1) dla tej klasy. Warunek
    /// triggera może to nadpisać.</summary>
    public double RecommendedMinConfidence { get; set; } = 0.30;

    /// <summary>Czy klasa jest built-in (z <c>DetectionClassSeeder</c>). Read-only w UI;
    /// user kopiuje jako bazę własnej klasy. Przy re-seeding identyfikowana przez
    /// <see cref="BuiltInKey"/>, nie nadpisuje user-edits.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Stabilny slug dla built-in klasy (np. "ppe-no-helmet"). Null dla user-custom.</summary>
    public string? BuiltInKey { get; set; }

    /// <summary>Wskaźnik do minimalnych wymagań modelu — flagi które model MUSI mieć
    /// aby obsłużyć tę klasę. Wyliczane z <see cref="Kind"/>:
    ///   ClosedSetBinding → ClosedSet; Text → TextPrompts; Visual → VisualPrompts;
    ///   TextAndVisual → TextPrompts | VisualPrompts.</summary>
    public ModelCapabilities RequiredCapabilities => Kind switch
    {
        DetectionClassKind.ClosedSetBinding => ModelCapabilities.ClosedSet,
        DetectionClassKind.Text => ModelCapabilities.TextPrompts,
        DetectionClassKind.Visual => ModelCapabilities.VisualPrompts,
        DetectionClassKind.TextAndVisual => ModelCapabilities.TextPrompts | ModelCapabilities.VisualPrompts,
        _ => ModelCapabilities.None
    };
}
