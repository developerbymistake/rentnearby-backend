using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers.Social;

namespace RentNearBy.Api.Endpoints;

// Bakhli social (CONTRACT_SOCIAL section 2). Every route SeekerOnly per route, except onboarding (any logged-in role).
public static class SocialEndpoints
{
    public static RouteGroupBuilder MapSocialEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/me", ProfileHandlers.GetMe).SeekerOnly();
        group.MapPut("/me/onboarding", ProfileHandlers.Onboard).RequireAuthorization();
        group.MapPut("/me/profile", ProfileHandlers.UpdateProfile).SeekerOnly();
        group.MapPut("/me/privacy", ProfileHandlers.UpdatePrivacy).SeekerOnly();
        group.MapPut("/me/area", ProfileHandlers.UpdateArea).SeekerOnly();
        group.MapPost("/me/guidelines", ProfileHandlers.AcceptGuidelines).SeekerOnly();
        group.MapGet("/travellers/{userId:guid}", ProfileHandlers.GetTraveller).SeekerOnly();

        group.MapPut("/travellers/{userId:guid}/follow", FollowHandlers.Follow).SeekerOnly();
        group.MapDelete("/travellers/{userId:guid}/follow", FollowHandlers.Unfollow).SeekerOnly();
        group.MapGet("/followers", FollowHandlers.Followers).SeekerOnly();
        group.MapGet("/following", FollowHandlers.Following).SeekerOnly();
        group.MapDelete("/followers/{userId:guid}", FollowHandlers.RemoveFollower).SeekerOnly();
        group.MapGet("/follow-requests", FollowHandlers.Requests).SeekerOnly();
        group.MapPost("/follow-requests/{userId:guid}/accept", FollowHandlers.AcceptRequest).SeekerOnly();
        group.MapPost("/follow-requests/{userId:guid}/decline", FollowHandlers.DeclineRequest).SeekerOnly();
        group.MapPut("/buddies/{userId:guid}", FollowHandlers.AddBuddy).SeekerOnly();

        group.MapGet("/trips/feed", PublicTripHandlers.Feed).SeekerOnly();
        group.MapGet("/trips/rail", PublicTripHandlers.Rail).SeekerOnly();
        group.MapGet("/trips/{tripId:guid}", PublicTripHandlers.Get).SeekerOnly();
        group.MapPost("/trips", PublicTripHandlers.Create).SeekerOnly();
        group.MapPut("/trips/{tripId:guid}/post", PublicTripHandlers.PutPost).SeekerOnly();

        group.MapPost("/trips/{tripId:guid}/join-requests", JoinHandlers.Send).SeekerOnly();
        group.MapDelete("/trips/{tripId:guid}/join-requests/mine", JoinHandlers.CancelMine).SeekerOnly();
        group.MapGet("/trips/{tripId:guid}/join-requests", JoinHandlers.List).SeekerOnly();
        group.MapPost("/join-requests/{id:guid}/accept", JoinHandlers.Accept).SeekerOnly();
        group.MapPost("/join-requests/{id:guid}/decline", JoinHandlers.Decline).SeekerOnly();

        group.MapGet("/chat/rooms", SocialChatHandlers.Rooms).SeekerOnly();
        group.MapGet("/chat/with/{userId:guid}", SocialChatHandlers.WithUser).SeekerOnly();
        group.MapGet("/chat/trip/{tripId:guid}", SocialChatHandlers.TripRoom).SeekerOnly();
        group.MapGet("/chat/rooms/{roomId:guid}", SocialChatHandlers.Room).SeekerOnly();
        group.MapPost("/chat/rooms", SocialChatHandlers.Start).SeekerOnly();
        group.MapGet("/chat/rooms/{roomId:guid}/messages", SocialChatHandlers.Messages).SeekerOnly();
        group.MapPost("/chat/rooms/{roomId:guid}/messages", SocialChatHandlers.Send).SeekerOnly();
        group.MapPost("/chat/rooms/{roomId:guid}/read", SocialChatHandlers.Read).SeekerOnly();
        group.MapDelete("/chat/rooms/{roomId:guid}", SocialChatHandlers.Delete).SeekerOnly();

        group.MapPost("/reports", SocialReportHandlers.Report).SeekerOnly();
        return group;
    }
}
