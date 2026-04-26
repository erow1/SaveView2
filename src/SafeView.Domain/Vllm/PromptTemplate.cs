using SafeView.Domain.Common;

namespace SafeView.Domain.Vllm;

/// <summary>
/// Gotowy szablon promptu do walidacji VLLM — reużywalny w wielu triggerach.
/// Szablon niesie pełny kontrakt: system prompt, user template (z placeholderami),
/// JSON schema odpowiedzi, rekomendowany próg pewności i domyślną minimalną severity.
///
/// Built-in szablony ładowane przy starcie przez <c>PromptTemplateSeeder</c> (read-only
/// dla usera — <see cref="IsBuiltIn"/> = true). User może stworzyć własne (IsBuiltIn=false)
/// i edytować/usuwać.
/// </summary>
public sealed class PromptTemplate : Entity
{
    /// <summary>Wyświetlana nazwa, unikalna w ramach kategorii. Np. "PPE — brak kasku".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Krótki opis scenariusza (1-3 zdania, PL) — pokazywany przy wyborze szablonu.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Kategoria funkcjonalna — grupuje szablony w UI (np. "PPE", "Pożar", "Zone", "Ruch").</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>System prompt (EN) — role definition + guidelines + severity rubric.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// User prompt template (PL) z placeholderami: {camera}, {zone}, {trigger}, {labels}, {detections}, {time_of_day}.
    /// Tekst skierowany do operatora trafi do finalnej odpowiedzi (reason/observations) po polsku.
    /// </summary>
    public string UserTemplate { get; set; } = string.Empty;

    /// <summary>JSON schema odpowiedzi (strict). Wymagane pola: confirmed, confidence, severity, reason.</summary>
    public string SchemaJson { get; set; } = string.Empty;

    /// <summary>Sugerowany próg pewności dla tego scenariusza (0..1). User może nadpisać.</summary>
    public double RecommendedMinConfidence { get; set; } = 0.7;

    /// <summary>Minimalna severity przy której trigger ma wypalić (domyślnie Medium — szablon może doradzić mocniej/słabiej).</summary>
    public ResponseSeverity DefaultMinSeverity { get; set; } = ResponseSeverity.Medium;

    /// <summary>
    /// Jak traktować <c>image_quality=poor</c>:
    ///  • true → nie odpalaj akcji (bezpieczniej nie alarmować gdy LLM sam mówi "nie widzę dobrze")
    ///  • false → odpalaj pomimo — użyteczne dla krytycznych scenariuszy (pożar) gdzie lepiej false alarm niż pominąć
    /// </summary>
    public bool RequireGoodImageQuality { get; set; } = true;

    /// <summary>
    /// Czy szablon jest wbudowany (z <c>PromptTemplateSeeder</c>). Built-in są read-only dla usera —
    /// może go skopiować jako bazę własnego ale nie edytować/usunąć. Pozwala updateować szablony
    /// między wersjami aplikacji bez niszczenia customowych prompów usera.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Slug niezmienialny dla built-in szablonów (identyfikuje przy re-seeding). Null dla user-custom.</summary>
    public string? BuiltInKey { get; set; }
}
