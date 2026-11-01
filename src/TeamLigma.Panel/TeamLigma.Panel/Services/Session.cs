using TeamLigma.Shared;

namespace TeamLigma.Panel.Services;

/// <summary>The signed-in user, as last reported by the server. The server enforces every permission;
/// this only decides which controls are shown as available.</summary>
public static class Session
{
    public static UserInfo? User { get; set; }
    public static bool IsAdmin => User?.Role == Roles.Admin;
    public static bool CanOperate => User?.Role is Roles.Admin or Roles.Operator;

    public static string RoleLabel(string? role) => role switch
    {
        Roles.Admin => "Administrator",
        Roles.Operator => "Operator",
        Roles.Viewer => "Viewer",
        _ => role ?? "",
    };
}
