using SafeView.Domain.Common;

namespace SafeView.Domain.Detection;

/// <summary>
/// <para>Skompilowany pack text embeddings dla YOLO-World — zestaw pre-computed wektorów CLIP
/// dla konkretnej listy <see cref="DetectionClass"/>. Gdy pack istnieje i pasuje do użytego
/// zestawu promptów, <c>OnnxYoloWorldDetector</c> pomija fazę CLIP-encode i ładuje embeddings
/// bezpośrednio z packa — prędkość inferencji zbliżona do closed-set.</para>
///
/// <para><b>Dlaczego nie prawdziwy re-param ONNX grafu?</b> Modyfikacja grafu w C# wymagałaby
/// Python sidecar-a albo dodatkowej zależności (OnnxSharp). Persistent embeddings cache daje
/// identyczny zysk performance (koszt CLIP encode ~10-30ms per klatka znika) bez narzutu
/// architektonicznego. Pełna re-param dostępna jako follow-up ticket gdy pojawi się realna potrzeba.</para>
///
/// <para><b>Invalidacja</b>: pack przestaje być ważny gdy (a) zmieni się <c>TextPrompt</c> któregoś
/// z klas referenced w <see cref="ClassIds"/>, albo (b) zmieni się encoder
/// (<see cref="EncoderBackend"/>). W obu przypadkach user musi re-compile pack — UI pokazuje ostrzeżenie.</para>
/// </summary>
public sealed class CompiledPromptPack : Entity
{
    /// <summary>User-friendly nazwa (np. "BHP PPE — kask + kamizelka").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Opcjonalny opis (do kontekstu w UI).</summary>
    public string? Description { get; set; }

    /// <summary>ID źródłowego <c>MLModel</c> (typowo YOLO-World v2-s). Pack jest ważny
    /// tylko dla tego modelu — inne modele mogą mieć inne przestrzenie embedding.</summary>
    public string SourceModelId { get; set; } = string.Empty;

    /// <summary>Backend encoder-a użyty do obliczenia embeddings (np. "onnx-clip-vit-b32",
    /// "external-llm-embeddings"). Używane do invalidation gdy user zmieni strategy.</summary>
    public string EncoderBackend { get; set; } = string.Empty;

    /// <summary>Wymiar pojedynczego wektora (typowo 512 dla CLIP ViT-B/32).</summary>
    public int EmbeddingDimension { get; set; }

    /// <summary>ID klas detekcji <b>w kolejności w jakiej zostały zakodowane</b>.
    /// Pipeline używa tego żeby zmapować index-klasy z output modelu na <c>DetectionClass.Id</c>.</summary>
    public List<string> ClassIds { get; set; } = [];

    /// <summary>Prompty tekstowe użyte do encode — audit / debug. Kolejność zgodna z <see cref="ClassIds"/>.</summary>
    public List<string> Prompts { get; set; } = [];

    /// <summary>Flat array embeddingów jako bytes (little-endian float32, row-major).
    /// Długość = <c>ClassIds.Count * EmbeddingDimension * 4</c>.</summary>
    public byte[] EmbeddingsBlob { get; set; } = [];

    public DateTime CompiledAt { get; set; } = DateTime.UtcNow;

    /// <summary>Czy pack został unieważniony (np. user zmienił prompt którejś klasy).
    /// Ustawiane przez <c>PromptPackInvalidator</c>. Gdy true — UI pokazuje "wymaga rekompilacji".</summary>
    public bool Stale { get; set; }

    /// <summary>Snapshot promptów w momencie kompilacji — porównanie z aktualnymi
    /// <see cref="DetectionClass.TextPrompt"/> pozwala wykryć stale pack. Klucz = ClassId,
    /// wartość = prompt text.</summary>
    public Dictionary<string, string> PromptSnapshots { get; set; } = new();
}
