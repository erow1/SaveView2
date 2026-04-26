using SafeView.Domain.SystemEvents;

namespace SafeView.Application.Configuration;

/// <summary>
/// Konfiguracja zapisu zdarzeń systemowych do MongoDB (kolekcja system_events).
/// Czytana przez Serilog sink oraz ISystemEventLogger.
/// </summary>
public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>Czy zapisywać zdarzenia do bazy (master switch). Domyślnie true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimalny poziom zapisywany do bazy. Domyślnie Warning — zapisujemy Warning/Error/Critical,
    /// pomijamy Debug i Info żeby nie zalewać kolekcji. Konfigurowalne w /admin/settings.
    /// </summary>
    public SystemEventSeverity MinimumLevel { get; set; } = SystemEventSeverity.Warning;

    /// <summary>
    /// Okres retencji — MongoDB TTL index automatycznie usuwa rekordy starsze. Domyślnie 7 dni.
    /// Zmiana wymaga restartu (TTL index jest zakładany przy starcie aplikacji).
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// Max długość StackTrace zapisywanego do bazy (żeby nie marnować miejsca na tasiemce).
    /// Domyślnie 8 KB — wystarcza na ~100 ramek.
    /// </summary>
    public int MaxStackTraceLength { get; set; } = 8 * 1024;
}
