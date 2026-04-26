using SafeView.Licensing.Model;

namespace SafeView.Licensing;

/// <summary>
/// Wysokopoziomowe API licencji — ładowanie, walidacja, dostęp do flag i limitów.
/// Rejestrowane jako singleton; wewnętrznie cache'uje ostatnio załadowaną licencję.
/// </summary>
public interface ILicenseService
{
    /// <summary>Aktualny status licencji (cache).</summary>
    LicenseStatus Status { get; }

    /// <summary>Podgląd załadowanej licencji — null gdy brak/niewczytana.</summary>
    LicenseFile? Current { get; }

    /// <summary>Odcisk palca (SHA-256[..16]) pliku .lic — null gdy brak.</summary>
    string? Fingerprint { get; }

    /// <summary>Ładuje i weryfikuje plik .lic z dysku. Aktualizuje cache i Status.</summary>
    Task<LicenseStatus> LoadAsync(CancellationToken ct = default);

    /// <summary>Czy dany moduł (np. "MOD.PPE") jest aktywny. Zwraca false gdy licencja nieważna.</summary>
    bool IsModuleEnabled(string moduleCode);

    /// <summary>Limit numeryczny (np. "cameras", "users"); -1 gdy brak klucza / licencja nieważna.</summary>
    long GetLimit(string limitKey);

    /// <summary>Zdarzenie emitowane po przeładowaniu licencji (np. przez LicenseMonitor).</summary>
    event EventHandler<LicenseStatus>? StatusChanged;
}

public enum LicenseState
{
    /// <summary>Brak pliku .lic na dysku.</summary>
    Missing,
    /// <summary>Plik istnieje, ale nie przeszedł walidacji (HMAC/AES/JSON).</summary>
    Invalid,
    /// <summary>Licencja ważna, ale wygasła.</summary>
    Expired,
    /// <summary>Licencja ważna.</summary>
    Valid
}

public sealed class LicenseStatus
{
    public LicenseState State { get; init; }
    public string? Message { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public int? DaysRemaining { get; init; }
    public string? CustomerName { get; init; }
    public string? Fingerprint { get; init; }
    public IReadOnlyList<string> Modules { get; init; } = [];

    public bool IsValid => State == LicenseState.Valid;

    public static LicenseStatus MissingStatus(string msg) =>
        new() { State = LicenseState.Missing, Message = msg };

    public static LicenseStatus InvalidStatus(string msg) =>
        new() { State = LicenseState.Invalid, Message = msg };
}
