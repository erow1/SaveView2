using SafeView.Domain.Common;

namespace SafeView.Domain.Users;

public sealed class Role : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Permissions { get; set; } = [];
    public bool IsBuiltIn { get; set; }

    public static class BuiltInNames
    {
        public const string Admin = "Admin";
        public const string Operator = "Operator";
        public const string Viewer = "Viewer";
    }
}
