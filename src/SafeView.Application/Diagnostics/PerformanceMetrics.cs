using System.Collections.Concurrent;
using SafeView.Application.Abstractions.Diagnostics;
using SafeView.Application.Abstractions.Time;

namespace SafeView.Application.Diagnostics;

/// <summary>
/// In-memory rolling window dla <see cref="IPerformanceMetrics"/>.
///
/// Trzyma próbki w <see cref="ConcurrentQueue{T}"/>, czyści stare przy <c>Record</c>
/// (lazy cleanup — żadne tło). Max window = 15 minut, max samples = 50k
/// (chroni przed OOM gdy pipeline jest bardzo aktywny).
///
/// Singleton, thread-safe. Stan tracony przy restarcie aplikacji (celowe — restart = świeża baseline).
/// </summary>
public sealed class PerformanceMetrics : IPerformanceMetrics
{
    private const int MaxSamples = 50_000;
    private static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(15);

    private readonly ConcurrentQueue<PerformanceSample> _samples = new();
    private readonly IClock _clock;

    public PerformanceMetrics(IClock clock)
    {
        _clock = clock;
    }

    public void Record(PerformanceSample sample)
    {
        _samples.Enqueue(sample);

        // Lazy cleanup — przy każdym Record usuwamy stare próbki (starsze niż MaxWindow)
        var cutoff = _clock.UtcNow - MaxWindow;
        while (_samples.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
        {
            _samples.TryDequeue(out _);
        }

        // Guard na max count
        while (_samples.Count > MaxSamples)
            _samples.TryDequeue(out _);
    }

    public PerformanceSnapshot GetSnapshot(TimeSpan window)
    {
        if (window > MaxWindow) window = MaxWindow;
        if (window <= TimeSpan.Zero) window = TimeSpan.FromMinutes(1);

        var now = _clock.UtcNow;
        var cutoff = now - window;

        // Filtruj do okna
        var all = _samples.Where(s => s.Timestamp >= cutoff).ToList();

        // Tylko pipeline-level samples liczą się do FramesPerMinute/AvgLatency ogólnego
        var pipelineOnly = all.Where(s => s.Stage == "pipeline").ToList();

        var totalSamples = all.Count;
        var totalErrors = all.Count(s => !s.Success);

        double avgLatency = pipelineOnly.Count > 0
            ? pipelineOnly.Average(s => s.DurationMs)
            : 0;
        int p95 = pipelineOnly.Count > 0
            ? Percentile(pipelineOnly.Select(s => s.DurationMs).ToArray(), 0.95)
            : 0;
        double framesPerMin = pipelineOnly.Count > 0
            ? pipelineOnly.Count / window.TotalMinutes
            : 0;

        return new PerformanceSnapshot(
            GeneratedAt: now,
            Window: window,
            TotalSamples: totalSamples,
            TotalErrors: totalErrors,
            AvgLatencyMs: avgLatency,
            P95LatencyMs: p95,
            FramesPerMinute: framesPerMin,
            PerCamera: Aggregate(all, s => s.CameraId),
            PerRoi: Aggregate(all, s => s.RoiId),
            PerModel: Aggregate(all, s => s.ModelId),
            PerStage: Aggregate(all, s => s.Stage));
    }

    private static List<PerformanceStats> Aggregate(List<PerformanceSample> samples, Func<PerformanceSample, string?> keyFn)
    {
        return samples
            .Where(s => !string.IsNullOrEmpty(keyFn(s)))
            .GroupBy(keyFn)
            .Select(g =>
            {
                var durations = g.Select(s => s.DurationMs).OrderBy(x => x).ToArray();
                return new PerformanceStats(
                    Key: g.Key!,
                    Label: null,
                    Count: g.Count(),
                    Errors: g.Count(s => !s.Success),
                    AvgLatencyMs: durations.Length > 0 ? durations.Average() : 0,
                    P50LatencyMs: Percentile(durations, 0.5),
                    P95LatencyMs: Percentile(durations, 0.95),
                    MaxLatencyMs: durations.Length > 0 ? durations[^1] : 0,
                    AvgDetections: g.Any() ? g.Average(s => s.DetectionCount) : 0);
            })
            .OrderByDescending(s => s.AvgLatencyMs)
            .ToList();
    }

    private static int Percentile(int[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return sorted[idx];
    }
}
