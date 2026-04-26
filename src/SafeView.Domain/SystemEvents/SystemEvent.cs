using SafeView.Domain.Common;

namespace SafeView.Domain.SystemEvents;

/// <summary>
/// Zdarzenie systemowe — loguje błędy techniczne, ostrzeżenia i problemy infrastrukturalne.
/// Zapisywane do kolekcji <c>system_events</c> z TTL 7 dni (MongoDB sam usuwa stare rekordy).
///
/// W odróżnieniu od <see cref="Audit.AuditEntry"/> (akcje użytkowników — kto co zrobił):
/// SystemEvent to "co się zepsuło w systemie" — crashy procesów, błędy ffmpeg/mediamtx,
/// problemy z MongoDB, storage permission errors itp.
/// </summary>
public sealed class SystemEvent : Entity
{
    /// <summary>Waga zdarzenia — Warning/Error/Critical trafia do UI jako alert.</summary>
    public SystemEventSeverity Severity { get; set; }

    /// <summary>Źródło — nazwa komponentu, np. "MediaMtxManager", "FfmpegSnapshotService".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Kategoria — do grupowania: "Camera", "Storage", "Database", "Network", "Startup".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Krótki, czytelny komunikat.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Typ wyjątku (np. "System.IO.IOException") — null gdy zdarzenie nie pochodzi z exception.</summary>
    public string? ExceptionType { get; set; }

    /// <summary>Treść wyjątku (ex.Message).</summary>
    public string? ExceptionMessage { get; set; }

    /// <summary>Stack trace — może być długi, UI pokazuje go po rozwinięciu wiersza.</summary>
    public string? StackTrace { get; set; }

    /// <summary>
    /// Dodatkowy kontekst jako płaska mapa string→string (CameraId, Url, Path, PID itp.).
    /// Używaj zamiast wstrzykiwać dane do Message — łatwiej filtrować w UI.
    /// </summary>
    public Dictionary<string, string> Context { get; set; } = [];

    /// <summary>Nazwa maszyny (Environment.MachineName).</summary>
    public string? MachineName { get; set; }

    /// <summary>PID procesu SafeView.Web.</summary>
    public int ProcessId { get; set; }
}

/// <summary>
/// Poziomy zdarzeń — zgodne semantycznie z Serilog LogEventLevel, żeby sink
/// mógł prosto mapować (Verbose/Debug/Information/Warning/Error/Fatal).
/// </summary>
public enum SystemEventSeverity
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}
