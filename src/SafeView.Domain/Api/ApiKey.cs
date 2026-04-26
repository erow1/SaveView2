using SafeView.Domain.Common;

namespace SafeView.Domain.Api;

/// <summary>
/// Klucz API dla integracji zewnętrznych (MOD.API).
/// Sekret nie jest przechowywany w czystej postaci — tylko jego SHA-256 (key jest wysokoentropijny).
/// </summary>
public sealed class ApiKey : Entity
{
    /// <summary>Nazwa nadana przez admina (np. "ERP integration", "SIEM Splunk").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Krótkie ID widoczne w UI, by ułatwić identyfikację (pierwsze 8 znaków klucza).</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 hex (lowercase) całego klucza.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Lista permissions — np. "api:incidents:read", "api:cameras:read".</summary>
    public List<string> Scopes { get; set; } = [];

    public bool Enabled { get; set; } = true;

    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
