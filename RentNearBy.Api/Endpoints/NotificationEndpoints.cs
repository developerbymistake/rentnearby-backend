using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/register-token", NotificationHandlers.RegisterToken).RequireAuthorization();
        group.MapDelete("/register-token", NotificationHandlers.UnregisterToken).RequireAuthorization();

        // Consumer-facing notification inbox — see NotificationInboxHandlers.cs.
        group.MapGet("", NotificationInboxHandlers.GetMyNotifications).RequireAuthorization();
        group.MapGet("/unread-count", NotificationInboxHandlers.GetUnreadCount).RequireAuthorization();
        group.MapPut("/{id:guid}/read", NotificationInboxHandlers.MarkNotificationRead).RequireAuthorization();
        group.MapPut("/read-all", NotificationInboxHandlers.MarkAllNotificationsRead).RequireAuthorization();

        return group;
    }
}
