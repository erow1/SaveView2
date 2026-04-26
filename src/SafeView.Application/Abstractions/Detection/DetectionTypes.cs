using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Pojedyncza detekcja z modelu — bbox w koordynatach [0..1] kamery, label, confidence.
/// W DetectionPipeline zbierane ze wszystkich ROI × modeli, potem filtrowane per Zone.
/// </summary>
public sealed record DetectionResult(
    string ModelId,
    string Label,
    double Confidence,
    Bbox Bbox);

/// <summary>
/// Wynik sprawdzenia Triggera — czy wypalił, a jeśli nie to dlaczego.
/// </summary>
public sealed record TriggerEvaluationResult(
    bool Fired,
    ActionSkipReason SkipReason = ActionSkipReason.None,
    string? Details = null)
{
    public static TriggerEvaluationResult Ok() => new(true);
    public static TriggerEvaluationResult Skipped(ActionSkipReason reason, string? details = null)
        => new(false, reason, details);
}

/// <summary>
/// Kontekst przekazywany do <c>IActionHandler</c> — wszystko czego handler może potrzebować
/// żeby wykonać akcję (wysłać email, zapalić log, wpisać notyfikację).
/// </summary>
public sealed class ActionContext
{
    public required string CameraId { get; init; }
    public required string CameraName { get; init; }
    public required string ZoneId { get; init; }
    public required string ZoneName { get; init; }
    public required string TriggerId { get; init; }
    public required string TriggerName { get; init; }
    public required string FrameSnapshotPath { get; init; }
    public required IReadOnlyList<DetectionResult> Detections { get; init; }
    public required DateTime OccurredAt { get; init; }
}
