using SafeView.Domain.Vllm;

namespace SafeView.Application.Abstractions.Vllm;

/// <summary>
/// Generator szablonów VLLM na podstawie opisu w języku naturalnym.
/// User opisuje scenariusz ("chcę wykrywać czy operator wózka widłowego nosi kamizelkę"),
/// LLM generuje kompletny <see cref="GeneratedTemplate"/> z system prompt / user template / schema /
/// rekomendowanymi progami. User dostaje to w playgroundzie, testuje, zapisuje.
/// </summary>
public interface IPromptGenerator
{
    Task<GeneratedTemplate> GenerateAsync(string userDescription, CancellationToken ct = default);

    /// <summary>
    /// Bierze istniejący szablon (już wygenerowany albo wczytany z biblioteki) oraz konkretny feedback
    /// użytkownika ("popraw żeby nie mylił pary wodnej z dymem", "podbij czułość na mniejsze obiekty")
    /// i generuje poprawioną wersję — tylko pola które mają realnie się zmienić.
    /// </summary>
    Task<GeneratedTemplate> RefineAsync(GeneratedTemplate current, string feedback, CancellationToken ct = default);
}

/// <summary>
/// Wynik generacji. Gdy <see cref="ErrorMessage"/> != null, pozostałe pola są nieokreślone
/// — UI pokazuje błąd, nie pre-populuje formularza.
/// </summary>
public sealed record GeneratedTemplate(
    string Name,
    string Category,
    string Description,
    string SystemPrompt,
    string UserTemplate,
    string SchemaJson,
    double RecommendedMinConfidence,
    ResponseSeverity DefaultMinSeverity,
    bool RequireGoodImageQuality,
    string? ErrorMessage = null);
