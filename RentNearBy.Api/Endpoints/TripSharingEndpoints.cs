using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers.Buddy.Be2;

namespace RentNearBy.Api.Endpoints;

// Trip sharing S1 to S17 on the /api/v1/trips group (TripPlannerGateFilter on the group). Literal segments "shared",
// "invites" and "invite-options" do not clash with /{id:guid}.
public static class TripSharingEndpoints
{
    public static RouteGroupBuilder MapTripSharingEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/shared", TripSharingHandlers.Shared).SeekerOnly();
        group.MapGet("/slots", TripSlotHandlers.Get).SeekerOnly();
        group.MapGet("/invite-options", TripSharingHandlers.InviteOptions).SeekerOnly();
        group.MapGet("/invites/{memberId:guid}", TripSharingHandlers.Preview).SeekerOnly();
        group.MapPost("/invites/{memberId:guid}/accept", TripSharingHandlers.Accept).SeekerOnly();
        group.MapPost("/invites/{memberId:guid}/decline", TripSharingHandlers.Decline).SeekerOnly();
        group.MapGet("/{id:guid}/members", TripSharingHandlers.Members).SeekerOnly();
        group.MapDelete("/{id:guid}/members/{userId:guid}", TripSharingHandlers.Remove).SeekerOnly();
        group.MapGet("/{id:guid}/members/{userId:guid}/expense-count", TripSharingHandlers.ExpenseCount).SeekerOnly();
        group.MapPost("/{id:guid}/invites", TripSharingHandlers.Invite).SeekerOnly();
        group.MapDelete("/{id:guid}/invites/{userId:guid}", TripSharingHandlers.CancelInvite).SeekerOnly();
        group.MapPost("/{id:guid}/leave", TripSharingHandlers.Leave).SeekerOnly();
        group.MapGet("/{id:guid}/view", TripSharingHandlers.View).SeekerOnly();
        return group;
    }
}
