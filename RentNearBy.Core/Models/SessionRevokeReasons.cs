namespace RentNearBy.Core.Models;

// D-34 / FB-42: a session the server ended answers 401 with this reason as error.type, so the app can say why.
public static class SessionRevokeReasons
{
    public const string SessionRoleChanged = "SessionRoleChanged";
    public const string AccountSuspended = "AccountSuspended";
    public const int MaxLength = 32;

    public static string MessageOf(string reason) => reason switch
    {
        SessionRoleChanged => "Role updated. Please log in again.",
        AccountSuspended => "Your account is paused.",
        _ => "Session expired. Please log in again.",
    };

    public static bool IsKnown(string? reason) => reason is SessionRoleChanged or AccountSuspended;
}
