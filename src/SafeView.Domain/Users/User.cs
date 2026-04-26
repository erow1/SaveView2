using SafeView.Domain.Common;

namespace SafeView.Domain.Users;

public sealed class User : Entity
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Hash Argon2id — nigdy plaintext.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Nazwy ról (referencja po nazwie, nie ID — bardziej czytelne dla audytu).</summary>
    public List<string> Roles { get; set; } = [];

    public bool IsActive { get; set; } = true;
    public bool IsLocked { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LastLoginAt { get; set; }

    /// <summary>MFA — puste w MVP; pole gotowe pod implementację po MVP (decyzja 0.1).</summary>
    public string? MfaSecret { get; set; }

    /// <summary>Preferowany język UI: "pl" lub "en".</summary>
    public string PreferredLanguage { get; set; } = "pl";
}
