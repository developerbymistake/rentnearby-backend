using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers.Buddy.Be1;

namespace RentNearBy.Api.Endpoints;

// Lane BE1 routes (contract 2.1). Not tab-gated; every user route is SeekerOnly, the admin route AdminOnly.
public static class BuddyEndpoints
{
    public static RouteGroupBuilder MapBuddyEndpoints(this RouteGroupBuilder group)
    {
        // Social contract D3: the buddy request routes and /seen are retired (My Buddies is a one-way list picked from followers);
        // their handlers, services and table stay.
        group.MapPost("/find", BuddyHandlers.Find).SeekerOnly();
        group.MapGet("/", BuddyHandlers.GetBuddies).SeekerOnly();
        group.MapGet("/summary", BuddyHandlers.Summary).SeekerOnly();
        group.MapDelete("/{userId:guid}", BuddyHandlers.Remove).SeekerOnly();
        return group;
    }

    public static RouteGroupBuilder MapPersonSafetyEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{userId:guid}/block", UserBlockHandlers.Block).SeekerOnly();
        group.MapPost("/{userId:guid}/unblock", UserBlockHandlers.Unblock).SeekerOnly();
        group.MapPost("/{userId:guid}/report", UserReportHandlers.Report).SeekerOnly();
        group.MapGet("/me/blocks", UserBlockHandlers.MyBlocks).SeekerOnly();
        group.MapGet("/{userId:guid}/block-context", UserBlockHandlers.BlockContext).SeekerOnly();
        return group;
    }

    public static RouteGroupBuilder MapUserReportReasonEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", UserReportHandlers.Reasons).SeekerOnly();
        return group;
    }

    public static RouteGroupBuilder MapAdminBuddyEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/users/{id:guid}/buddy-hold", AdminBuddyHandlers.SetHold).RequireAuthorization("AdminOnly");
        return group;
    }
}
