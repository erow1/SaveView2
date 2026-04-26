namespace SafeView.Application.Abstractions.Diagnostics;

/// <summary>
/// Pojedyncza próbka wydajnościowa — jeden etap wykonania w pipeline.
/// </summary>
public sealed record PerformanceSample(
    DateTime Timestamp,
    string Stage,           // "pipeline" | "roi_inference" | "proposer" | "confirmer" | "vllm" | "action"
    string? CameraId,
    string? RoiId,
    string? ModelId,
    int DurationMs,
    int DetectionCount,
    bool Success,
    string? ErrorType = null);

/// <summary>
/// Agregat statystyk per-grupa (camera/roi/model/stage).
/// </summary>
public sealed record PerformanceStats(
    string Key,
    string? Label,
    int Count,
    int Errors,
    double AvgLatencyMs,
    int P50LatencyMs,
    int P95LatencyMs,
    int MaxLatencyMs,
    double AvgDetections);

/// <summary>
/// Snapshot agregowanych metryk dla okna czasowego (np. ostatnie 5 minut).
/// </summary>
public sealed record PerformanceSnapshot(
    DateTime GeneratedAt,
    TimeSpan Window,
    int TotalSamples,
    int TotalErrors,
    double AvgLatencyMs,
    int P95LatencyMs,
    double FramesPerMinute,
    IReadOnlyList<PerformanceStats> PerCamera,
    IReadOnlyList<PerformanceStats> PerRoi,
    IReadOnlyList<PerformanceStats> PerModel,
    IReadOnlyList<PerformanceStats> PerStage);

/// <summary>
/// Zbiera metryki wydajności z DetectionPipeline i innych komponentów.
/// In-memory rolling window — trzyma ostatnie N minut próbek, agreguje na żądanie.
/// Singleton, thread-safe.
/// </summary>
public interface IPerformanceMetrics
{
    /// <summary>Zapisuje próbkę (fire-and-forget — nie blokuje pipeline).</summary>
    void Record(PerformanceSample sample);

    /// <summary>
    /// Agreguje próbki z ostatniego <paramref name="window"/> czasu (np. 5 min).
    /// Wywoływane przez endpoint/dashboard.
    /// </summary>
    PerformanceSnapshot GetSnapshot(TimeSpan window);
}
