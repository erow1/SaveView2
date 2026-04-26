using SafeView.Domain.Users;

namespace SafeView.Security.Auth;

public sealed record AuthResult(bool Success, User? User, string? ErrorCode, string? ErrorMessage)
{
    public static AuthResult Ok(User user) => new(true, user, null, null);
    public static AuthResult Fail(string code, string message) => new(false, null, code, message);
}

public interface IAuthService
{
    Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct = default);
    Task<bool> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken ct = default);
}
