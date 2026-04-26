using SafeView.Domain.Common;

namespace SafeView.Domain.Detection;

/// <summary>
/// Status pojedynczego wykonania akcji.
/// </summary>
public enum ActionExecutionStatus
{
    Success = 0,
    Failed = 1,
    /// <summary>Pominięto z powodu cooldownu/schedule/rate-limit — nie jest to błąd.</summary>
    Skipped = 2,
    RateLimited = 3
}

/// <summary>Powód pominięcia akcji (gdy <see cref="ActionExecutionStatus.Skipped"/>).</summary>
public enum ActionSkipReason
{
    None = 0,
    Cooldown = 1,
    Schedule = 2,
    RateLimit = 3,
    VllmRejected = 4,
    ActionDisabled = 5,
    TriggerDisabled = 6
}

/// <summary>
/// Audit log pojedynczego wykonania akcji — kto/co/kiedy/z jakim wynikiem.
/// Kolekcja <c>action_executions</c> ma TTL index na <see cref="Entity.CreatedAt"/>
/// (retention 30 dni — konfigurowalne).
/// </summary>
public sealed class ActionExecution : Entity
{
    public string TriggerId { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>Nazwy (denormalized dla szybkich query w UI bez joinów).</summary>
    public string? TriggerName { get; set; }
    public string? ActionName { get; set; }
    public string? CameraName { get; set; }
    public string? ZoneName { get; set; }

    public ActionExecutionStatus Status { get; set; }
    public ActionSkipReason SkipReason { get; set; } = ActionSkipReason.None;
    public string? ErrorMessage { get; set; }

    /// <summary>Ścieżka do klatki która odpaliła trigger (relative, w <c>Frames</c> storage).</summary>
    public string? FrameSnapshotPath { get; set; }

    /// <summary>Zserializowane detekcje które uruchomiły trigger (JSON, debug).</summary>
    public string? DetectionsJson { get; set; }

    /// <summary>Czas wykonania handlera w ms.</summary>
    public int DurationMs { get; set; }
}
