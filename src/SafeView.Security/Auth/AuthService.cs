using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Security.Passwords;

namespace SafeView.Security.Auth;

public sealed class AuthService : IAuthService
{
    private const int MaxFailedAttempts = 5;

    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<AuthService> _log;

    public AuthService(IUserRepository users, IPasswordHasher hasher, ILogger<AuthService> log)
    {
        _users = users;
        _hasher = hasher;
        _log = log;
    }

    public async Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return AuthResult.Fail("invalid_input", "Podaj nazwę użytkownika i hasło.");

        var user = await _users.FindByUsernameAsync(username, ct).ConfigureAwait(false);
        if (user is null)
        {
            _log.LogWarning("Auth failed: unknown user {Username}", username);
            return AuthResult.Fail("unknown_user", "Nieprawidłowy login lub hasło.");
        }

        if (!user.IsActive)
            return AuthResult.Fail("inactive", "Konto nieaktywne.");

        if (user.IsLocked)
            return AuthResult.Fail("locked", "Konto zablokowane — skontaktuj się z administratorem.");

        if (!_hasher.Verify(password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= MaxFailedAttempts)
            {
                user.IsLocked = true;
                _log.LogWarning("User {Username} locked after {Attempts} failed attempts", username, user.FailedLoginAttempts);
            }
            await _users.UpdateAsync(user, ct).ConfigureAwait(false);
            return AuthResult.Fail("bad_password", "Nieprawidłowy login lub hasło.");
        }

        user.FailedLoginAttempts = 0;
        user.LastLoginAt = DateTime.UtcNow;
        await _users.UpdateAsync(user, ct).ConfigureAwait(false);
        return AuthResult.Ok(user);
    }

    public async Task<bool> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);
        var user = await _users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return false;
        if (!_hasher.Verify(currentPassword, user.PasswordHash)) return false;
        user.PasswordHash = _hasher.Hash(newPassword);
        await _users.UpdateAsync(user, ct).ConfigureAwait(false);
        return true;
    }
}
