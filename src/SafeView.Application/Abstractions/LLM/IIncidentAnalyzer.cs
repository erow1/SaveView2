using SafeView.Domain.Incidents;

namespace SafeView.Application.Abstractions.LLM;

/// <summary>
/// Analizuje pojedynczy incydent z użyciem LLM (structured output).
/// Implementacja zwraca model z wbudowaną oceną false-positive i rekomendacją.
/// </summary>
public interface IIncidentAnalyzer
{
    Task<IncidentLlmAnalysis?> AnalyzeAsync(Incident incident, CancellationToken ct = default);
}
