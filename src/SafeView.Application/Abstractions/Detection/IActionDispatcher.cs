using SafeView.Domain.Detection;

namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Wywołuje akcje dla wypalonego Triggera. Sprawdza rate-limit per akcja, wywołuje właściwy
/// <see cref="IActionHandler"/>, zapisuje <see cref="ActionExecution"/> audit log.
/// </summary>
public interface IActionDispatcher
{
    Task DispatchAsync(
        IReadOnlyList<DetectionAction> actions,
        ActionContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Ręczne wywołanie testowe — pomija gate'y <c>Enabled</c> i rate-limit,
    /// zawsze uruchamia handler. Audit log nadal zapisywany. Używane przez przycisk
    /// „Test fire" na listach akcji/triggerów.
    /// </summary>
    Task<ActionExecution> TestFireAsync(
        DetectionAction action,
        ActionContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Handler dla konkretnego typu akcji. Rejestrowany w DI jako keyed-singleton
/// (klucz = <see cref="Domain.Detection.ActionType"/>). Dispatcher wybiera handler wg typu.
/// </summary>
public interface IActionHandler
{
    /// <summary>Typ akcji który ten handler obsługuje.</summary>
    ActionType Type { get; }

    /// <summary>Wykonuje akcję. Rzuca wyjątek przy błędzie — dispatcher je złapie i zaloguje.</summary>
    Task HandleAsync(DetectionAction action, ActionContext context, CancellationToken ct = default);
}
