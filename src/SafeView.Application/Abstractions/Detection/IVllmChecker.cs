using SafeView.Domain.Detection;
using SafeView.Domain.Vllm;

namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Wynik walidacji VLLM — strukturyzowana odpowiedź którą potrafimy łatwo sparsować,
/// niezależnie od backendu LLM (vLLM/OpenAI/Ollama).
///
/// Schema odpowiedzi (Faza A+):
///   observations[] · alternative_explanations[] · severity · confidence · confirmed ·
///   image_quality · reason · recommended_action
///
/// Pola <see cref="Confirmed"/> i <see cref="Confidence"/> zostają dla kompatybilności
/// z obecnym pipeline'em. Nowe pola (severity/quality/action/observations) są opcjonalne —
/// gdy LLM zwróci stary schema, parser wypełnia wartości domyślne.
/// </summary>
public sealed record VllmCheckResult(
    bool Confirmed,
    double Confidence,
    string? Reason,
    string? RawResponse,
    bool IsError = false,
    ResponseSeverity Severity = ResponseSeverity.None,
    ImageQuality Quality = ImageQuality.Good,
    RecommendedAction Action = RecommendedAction.Ignore,
    IReadOnlyList<string>? Observations = null,
    IReadOnlyList<string>? AlternativeExplanations = null);

/// <summary>
/// Pyta VLLM "czy trigger to prawdziwy alert czy false-positive?" z dołączoną klatką (opcjonalnie).
/// Wywoływane w <c>DetectionPipeline</c> po trigger fire, przed dispatchem actions.
/// </summary>
public interface IVllmChecker
{
    Task<VllmCheckResult> CheckAsync(
        VllmCheckConfig config,
        ActionContext context,
        CancellationToken ct = default);
}
