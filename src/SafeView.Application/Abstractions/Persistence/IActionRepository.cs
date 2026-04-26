using SafeView.Domain.Detection;

namespace SafeView.Application.Abstractions.Persistence;

public interface IActionRepository : IRepository<DetectionAction>
{
    Task<IReadOnlyList<DetectionAction>> ListByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct = default);

    Task<IReadOnlyList<DetectionAction>> ListEnabledAsync(CancellationToken ct = default);
}

public interface IActionExecutionRepository
{
    Task LogAsync(ActionExecution execution, CancellationToken ct = default);

    Task<IReadOnlyList<ActionExecution>> QueryAsync(
        ActionExecutionFilter filter, int skip, int take, CancellationToken ct = default);

    Task<long> CountAsync(ActionExecutionFilter filter, CancellationToken ct = default);
}

/// <summary>Filtr dla historii wykonań akcji (UI /actions/history).</summary>
public sealed class ActionExecutionFilter
{
    public string? ActionId { get; set; }
    public string? TriggerId { get; set; }
    public string? CameraId { get; set; }
    public ActionExecutionStatus? Status { get; set; }
    public DateTime? Since { get; set; }
}
