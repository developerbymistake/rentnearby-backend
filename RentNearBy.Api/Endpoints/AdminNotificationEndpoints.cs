using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

// Admin's read-only system-wide notification feed, mounted at "/api/v1/admin/notifications" —
// separate from AdminAuthEndpoints' admin device-token register/unregister routes.
public static class AdminNotificationEndpoints
{
    public static RouteGroupBuilder MapAdminNotificationEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("", AdminNotificationHandlers.AdminGetNotifications).RequireAuthorization("AdminOnly");

        return group;
    }
}
