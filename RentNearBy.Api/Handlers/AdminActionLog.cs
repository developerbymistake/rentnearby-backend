using System.Security.Claims;

namespace RentNearBy.Api.Handlers;

// D-17 / FB-57: one structured line per admin action (ids only, no names, phones, reasons or amounts). No audit table in this
// round: one real admin; the log is the record.
public static class AdminActionLog
{
    public const string Category = "admin.action";

    public static void Write(HttpContext http, string action, Guid? targetId)
    {
        var adminId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(Category);
        logger?.LogInformation("admin.action {Action} {AdminId} {TargetId}", action, adminId, targetId);
    }
}
