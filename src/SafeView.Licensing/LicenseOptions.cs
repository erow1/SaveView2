namespace SafeView.Licensing;

/// <summary>
/// Konfiguracja modułu licencji (sekcja "License" w appsettings).
/// </summary>
public sealed class LicenseOptions
{
    public const string SectionName = "License";

    /// <summary>Absolutna lub relatywna ścieżka do pliku .lic.</summary>
    public string FilePath { get; set; } = "storage/licenses/safeview.lic";

    /// <summary>Interwał rewalidacji w minutach (domyślnie 60).</summary>
    public int RevalidateIntervalMinutes { get; set; } = 60;

    /// <summary>Dni przed wygaśnięciem, kiedy UI zacznie pokazywać ostrzeżenia.</summary>
    public int ExpiryWarningDays { get; set; } = 30;

    /// <summary>Gdy true — brak lub nieważna licencja przełącza system w tryb read-only zamiast hard-fail.</summary>
    public bool AllowReadOnlyOnInvalid { get; set; } = true;

    /// <summary>
    /// TYLKO DEV: gdy true, serwis licencji zwraca sztuczną ważną licencję ze wszystkimi modułami
    /// i hojnymi limitami — bez czytania pliku .lic. Musi być WYŁĄCZONE w Production.
    /// </summary>
    public bool DevBypass { get; set; }
}
