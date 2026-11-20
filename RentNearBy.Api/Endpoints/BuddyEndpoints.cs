using RentNearBy.Api.Handlers.Buddy.Be1;

namespace RentNearBy.Api.Endpoints;

// Lane BE1 routes (contract 2.1). Not tab-gated.
public static class BuddyEndpoints
{
    public static RouteGroupBuilder MapBuddyEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/find", BuddyHandlers.Find).RequireAuthorization();
        group.MapPost("/requests", BuddyHandlers.SendRequest).RequireAuthorization();
        group.MapGet("/requests", BuddyHandlers.GetRequests).RequireAuthorization();
        group.MapPost("/requests/{id:guid}/accept", BuddyHandlers.Accept).RequireAuthorization();
        group.MapPost("/requests/{id:guid}/decline", BuddyHandlers.Decline).RequireAuthorization();
        group.MapPost("/requests/{id:guid}/cancel", BuddyHandlers.Cancel).RequireAuthorization();
        group.MapGet("/", BuddyHandlers.GetBuddies).RequireAuthorization();
        group.MapPost("/seen", BuddyHandlers.Seen).RequireAuthorization();
        group.MapGet("/summary", BuddyHandlers.Summary).RequireAuthorization();
        group.MapDelete("/{userId:guid}", BuddyHandlers.Remove).RequireAuthorization();
        return group;
    }

    public static RouteGroupBuilder MapPersonSafetyEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{userId:guid}/block", UserBlockHandlers.Block).RequireAuthorization();
        group.MapPost("/{userId:guid}/unblock", UserBlockHandlers.Unblock).RequireAuthorization();
        group.MapPost("/{userId:guid}/report", UserReportHandlers.Report).RequireAuthorization();
        group.MapGet("/me/blocks", UserBlockHandlers.MyBlocks).RequireAuthorization();
        group.MapGet("/{userId:guid}/block-context", UserBlockHandlers.BlockContext).RequireAuthorization();
        return group;
    }

    public static RouteGroupBuilder MapUserReportReasonEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", UserReportHandlers.Reasons).RequireAuthorization();
        return group;
    }

    public static RouteGroupBuilder MapAdminBuddyEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/users/{id:guid}/buddy-hold", AdminBuddyHandlers.SetHold).RequireAuthorization("AdminOnly");
        return group;
    }
}
