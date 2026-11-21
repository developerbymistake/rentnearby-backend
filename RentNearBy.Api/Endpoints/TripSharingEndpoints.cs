using RentNearBy.Api.Handlers.Buddy.Be2;

namespace RentNearBy.Api.Endpoints;

// Trip sharing S1 to S17 on the /api/v1/trips group (TripPlannerGateFilter on the group). Literal segments "shared",
// "invites" and "invite-options" do not clash with /{id:guid}.
public static class TripSharingEndpoints
{
    public static RouteGroupBuilder MapTripSharingEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/shared", TripSharingHandlers.Shared).RequireAuthorization();
        group.MapGet("/slots", TripSlotHandlers.Get).RequireAuthorization();
        group.MapGet("/invite-options", TripSharingHandlers.InviteOptions).RequireAuthorization();
        group.MapGet("/invites/{memberId:guid}", TripSharingHandlers.Preview).RequireAuthorization();
        group.MapPost("/invites/{memberId:guid}/accept", TripSharingHandlers.Accept).RequireAuthorization();
        group.MapPost("/invites/{memberId:guid}/decline", TripSharingHandlers.Decline).RequireAuthorization();
        group.MapGet("/{id:guid}/members", TripSharingHandlers.Members).RequireAuthorization();
        group.MapDelete("/{id:guid}/members/{userId:guid}", TripSharingHandlers.Remove).RequireAuthorization();
        group.MapGet("/{id:guid}/members/{userId:guid}/expense-count", TripSharingHandlers.ExpenseCount).RequireAuthorization();
        group.MapPost("/{id:guid}/invites", TripSharingHandlers.Invite).RequireAuthorization();
        group.MapDelete("/{id:guid}/invites/{userId:guid}", TripSharingHandlers.CancelInvite).RequireAuthorization();
        group.MapPost("/{id:guid}/leave", TripSharingHandlers.Leave).RequireAuthorization();
        group.MapGet("/{id:guid}/view", TripSharingHandlers.View).RequireAuthorization();
        return group;
    }
}
