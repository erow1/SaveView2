using SafeView.Domain.Common;

namespace SafeView.Domain.Vllm;

/// <summary>
/// Snapshot szablonu VLLM sprzed ostatniej edycji. Zapisywany automatycznie przy każdym
/// <c>UpdateAsync</c> szablonu w <c>MongoPromptTemplateRepository</c> — historia pozwala
/// wrócić do poprzedniej wersji gdy nowy prompt okaże się gorszy.
///
/// Wersje nie mają własnych wersji (not meta-versioned). TTL index nie jest ustawiony —
/// historia dla szablonu trzymana jest bezterminowo. Przy skalach produkcyjnych można
/// dodać retention policy w konfiguracji.
/// </summary>
public sealed class PromptTemplateVersion : Entity
{
    /// <summary>ID szablonu do którego ta wersja należy (FK do <see cref="PromptTemplate"/>).</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>Snapshot pól szablonu w momencie zapisu.</summary>
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserTemplate { get; set; } = string.Empty;
    public string SchemaJson { get; set; } = string.Empty;
    public double RecommendedMinConfidence { get; set; }
    public ResponseSeverity DefaultMinSeverity { get; set; }
    public bool RequireGoodImageQuality { get; set; }

    /// <summary>Kto dokonał poprzedniej edycji (user id). Null gdy brak kontekstu HTTP.</summary>
    public string? ChangedByUserId { get; set; }

    /// <summary>Opcjonalny komentarz "co zmieniłem i dlaczego" (TODO: UI na razie nie wypełnia).</summary>
    public string? Comment { get; set; }
}
