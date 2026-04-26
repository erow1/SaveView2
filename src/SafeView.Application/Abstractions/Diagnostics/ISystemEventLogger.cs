using SafeView.Domain.SystemEvents;

namespace SafeView.Application.Abstractions.Diagnostics;

/// <summary>
/// Logger zdarzeń systemowych do persistent storage (MongoDB).
/// Best-effort — błąd zapisu nie może przerwać operacji biznesowej.
/// </summary>
public interface ISystemEventLogger
{
    /// <summary>Zapisuje pojedyncze zdarzenie.</summary>
    Task LogAsync(SystemEvent evt, CancellationToken ct = default);
}

/// <summary>
/// Zapytania na kolekcję system_events — dla UI (/admin/system-events).
/// </summary>
public interface ISystemEventQuery
{
    Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventFilter filter, int skip, int take, CancellationToken ct = default);
    Task<long> CountAsync(SystemEventFilter filter, CancellationToken ct = default);

    /// <summary>Usuwa wszystkie rekordy starsze niż X — backup gdyby TTL index zawiódł.</summary>
    Task<long> PurgeOlderThanAsync(TimeSpan retention, CancellationToken ct = default);
}

/// <summary>Filtr dla <see cref="ISystemEventQuery"/>.</summary>
public sealed class SystemEventFilter
{
    /// <summary>Minimalny poziom — pokaż tylko zdarzenia >= temu (np. Warning → pomiń Debug/Info).</summary>
    public SystemEventSeverity? MinSeverity { get; set; }

    /// <summary>Filtr po dokładnej nazwie źródła (case-insensitive).</summary>
    public string? Source { get; set; }

    /// <summary>Filtr po kategorii.</summary>
    public string? Category { get; set; }

    /// <summary>Wolny tekst — szuka w Message + ExceptionMessage (regex).</summary>
    public string? SearchText { get; set; }

    /// <summary>Dolna granica czasu (CreatedAt >= since).</summary>
    public DateTime? Since { get; set; }
}
