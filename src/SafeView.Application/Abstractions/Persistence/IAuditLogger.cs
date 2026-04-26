using SafeView.Domain.Audit;

namespace SafeView.Application.Abstractions.Persistence;

/// <summary>
/// Wpis do audytu — best effort, nie może przerwać operacji biznesowej.
/// Implementacja zapisuje asynchronicznie do MongoDB (kolekcja audit_log).
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(AuditEntry entry, CancellationToken ct = default);
}

public interface IAuditQuery
{
    Task<IReadOnlyList<AuditEntry>> QueryAsync(int skip = 0, int take = 100, string? userFilter = null, string? actionFilter = null, CancellationToken ct = default);
    Task<long> CountAsync(string? userFilter = null, string? actionFilter = null, CancellationToken ct = default);
}
