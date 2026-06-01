using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/register-token", NotificationHandlers.RegisterToken).RequireAuthorization();
        group.MapDelete("/register-token", NotificationHandlers.UnregisterToken).RequireAuthorization();

        return group;
    }
}
