namespace SafeView.Licensing.Model;

/// <summary>
/// Zawartość logiczna pliku licencyjnego — serializowana do JSON, szyfrowana AES-256-GCM,
/// z dodatkowym HMAC-SHA256 jako sygnatura.
/// </summary>
public sealed class LicenseFile
{
    public string CustomerId { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Lista aktywnych modułów (kody z <see cref="Modules.LicenseModules"/>).</summary>
    public List<string> Modules { get; set; } = [];

    /// <summary>Limity numeryczne (np. cameras, users).</summary>
    public Dictionary<string, long> Limits { get; set; } = new();

    public string? Notes { get; set; }
}
