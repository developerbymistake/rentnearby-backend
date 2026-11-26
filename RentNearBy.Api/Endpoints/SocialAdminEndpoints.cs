using RentNearBy.Api.Handlers.Social;

namespace RentNearBy.Api.Endpoints;

// Bakhli social admin routes (contract 2.8), AdminOnly per route.
public static class SocialAdminEndpoints
{
    public static RouteGroupBuilder MapSocialAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/reports/{id:guid}/snapshot", SocialAdminHandlers.Snapshot).RequireAuthorization("AdminOnly");
        group.MapPost("/social/trips/{tripId:guid}/unpublish", SocialAdminHandlers.Unpublish).RequireAuthorization("AdminOnly");
        group.MapPut("/users/{id:guid}/identity", SocialAdminHandlers.SetIdentity).RequireAuthorization("AdminOnly");
        return group;
    }
}
