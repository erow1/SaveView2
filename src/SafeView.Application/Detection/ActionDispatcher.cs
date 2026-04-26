using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Time;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection;

/// <summary>
/// Produkcyjna implementacja <see cref="IActionDispatcher"/>. Singleton.
///
/// Dla każdej akcji:
///  • Sprawdza rate-limit (sliding window per action)
///  • Wybiera <see cref="IActionHandler"/> pasujący do <see cref="ActionType"/>
///  • Wywołuje handler i mierzy czas
///  • Zapisuje <see cref="ActionExecution"/> audit log (zawsze, niezależnie od wyniku)
/// </summary>
public sealed class ActionDispatcher : IActionDispatcher
{
    private readonly Dictionary<ActionType, IActionHandler> _handlers;
    private readonly IActionExecutionRepository _audit;
    private readonly IClock _clock;
    private readonly ILogger<ActionDispatcher> _log;
    private readonly IFlowEventPublisher? _flow;

    // Rate limit: dla każdej akcji kolejka timestampów odpaleń w ciągu ostatniej minuty.
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _rateState = new();

    public ActionDispatcher(
        IEnumerable<IActionHandler> handlers,
        IActionExecutionRepository audit,
        IClock clock,
        ILogger<ActionDispatcher> log,
        IFlowEventPublisher? flow = null)
    {
        _handlers = handlers.ToDictionary(h => h.Type);
        _audit = audit;
        _clock = clock;
        _log = log;
        _flow = flow;
    }

    public async Task DispatchAsync(
        IReadOnlyList<DetectionAction> actions,
        ActionContext context,
        CancellationToken ct = default)
    {
        foreach (var action in actions)
        {
            await ExecuteOneAsync(action, context, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteOneAsync(DetectionAction action, ActionContext context, CancellationToken ct)
    {
        var baseExec = new ActionExecution
        {
            ActionId = action.Id,
            ActionName = action.Name,
            TriggerId = context.TriggerId,
            TriggerName = context.TriggerName,
            CameraId = context.CameraId,
            CameraName = context.CameraName,
            ZoneId = context.ZoneId,
            ZoneName = context.ZoneName,
            FrameSnapshotPath = context.FrameSnapshotPath,
            CreatedAt = context.OccurredAt
        };

        // 1. Enabled?
        if (!action.Enabled)
        {
            baseExec.Status = ActionExecutionStatus.Skipped;
            baseExec.SkipReason = ActionSkipReason.ActionDisabled;
            await _audit.LogAsync(baseExec, ct).ConfigureAwait(false);
            await PublishFlowAsync(action, context, baseExec, ct).ConfigureAwait(false);
            return;
        }

        // 2. Rate limit
        if (action.RateLimitPerMinute > 0 && !CheckRateLimit(action))
        {
            baseExec.Status = ActionExecutionStatus.RateLimited;
            baseExec.SkipReason = ActionSkipReason.RateLimit;
            await _audit.LogAsync(baseExec, ct).ConfigureAwait(false);
            await PublishFlowAsync(action, context, baseExec, ct).ConfigureAwait(false);
            _log.LogDebug("Action {Action} rate-limited ({Limit}/min)", action.Name, action.RateLimitPerMinute);
            return;
        }

        // 3. Handler exists?
        if (!_handlers.TryGetValue(action.Type, out var handler))
        {
            baseExec.Status = ActionExecutionStatus.Failed;
            baseExec.ErrorMessage = $"No handler registered for action type {action.Type}";
            await _audit.LogAsync(baseExec, ct).ConfigureAwait(false);
            await PublishFlowAsync(action, context, baseExec, ct).ConfigureAwait(false);
            _log.LogWarning("No IActionHandler registered for {Type}", action.Type);
            return;
        }

        // 4. Execute + time
        var sw = Stopwatch.StartNew();
        try
        {
            await handler.HandleAsync(action, context, ct).ConfigureAwait(false);
            sw.Stop();
            baseExec.Status = ActionExecutionStatus.Success;
            baseExec.DurationMs = (int)sw.ElapsedMilliseconds;
            await _audit.LogAsync(baseExec, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            baseExec.Status = ActionExecutionStatus.Failed;
            baseExec.ErrorMessage = ex.Message;
            baseExec.DurationMs = (int)sw.ElapsedMilliseconds;
            await _audit.LogAsync(baseExec, ct).ConfigureAwait(false);
            _log.LogError(ex, "Action {Action} ({Type}) failed", action.Name, action.Type);
        }
        await PublishFlowAsync(action, context, baseExec, ct).ConfigureAwait(false);
    }

    private Task PublishFlowAsync(DetectionAction action, ActionContext context, ActionExecution exec, CancellationToken ct)
    {
        if (_flow is null) return Task.CompletedTask;
        return _flow.ActionExecutedAsync(action.Id, context.TriggerId, exec.Status.ToString(), exec.DurationMs, ct);
    }

    public async Task<ActionExecution> TestFireAsync(DetectionAction action, ActionContext context, CancellationToken ct = default)
    {
        var exec = new ActionExecution
        {
            ActionId = action.Id,
            ActionName = action.Name,
            TriggerId = context.TriggerId,
            TriggerName = context.TriggerName,
            CameraId = context.CameraId,
            CameraName = context.CameraName,
            ZoneId = context.ZoneId,
            ZoneName = context.ZoneName,
            FrameSnapshotPath = context.FrameSnapshotPath,
            CreatedAt = context.OccurredAt
        };

        if (!_handlers.TryGetValue(action.Type, out var handler))
        {
            exec.Status = ActionExecutionStatus.Failed;
            exec.ErrorMessage = $"No handler registered for action type {action.Type}";
            await _audit.LogAsync(exec, ct).ConfigureAwait(false);
            return exec;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await handler.HandleAsync(action, context, ct).ConfigureAwait(false);
            sw.Stop();
            exec.Status = ActionExecutionStatus.Success;
            exec.DurationMs = (int)sw.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            sw.Stop();
            exec.Status = ActionExecutionStatus.Failed;
            exec.ErrorMessage = ex.Message;
            exec.DurationMs = (int)sw.ElapsedMilliseconds;
            _log.LogWarning(ex, "Manual test-fire of action {Action} ({Type}) failed", action.Name, action.Type);
        }
        await _audit.LogAsync(exec, ct).ConfigureAwait(false);
        return exec;
    }

    private bool CheckRateLimit(DetectionAction action)
    {
        var now = _clock.UtcNow;
        var windowStart = now.AddMinutes(-1);
        var queue = _rateState.GetOrAdd(action.Id, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < windowStart)
                queue.Dequeue();
            if (queue.Count >= action.RateLimitPerMinute)
                return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
