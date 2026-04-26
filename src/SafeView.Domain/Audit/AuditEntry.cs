using SafeView.Domain.Common;

namespace SafeView.Domain.Audit;

public sealed class AuditEntry : Entity
{
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = string.Empty;        // np. "user.login", "camera.create"
    public string? TargetType { get; set; }                   // np. "User", "Camera"
    public string? TargetId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public AuditOutcome Outcome { get; set; }
    public string? Details { get; set; }                      // JSON / tekst
}

public enum AuditOutcome
{
    Success,
    Failure,
    Denied
}
