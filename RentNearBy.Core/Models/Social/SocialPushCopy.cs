using System.Text.Json;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Models.Social;

// Contract 3.6 / 5: every push of the social feature. Lock-screen rule: a push names at most the actor; never message text,
// a note, a city or a date. Text lives only in the transient outbox row.
public static class SocialPushCopy
{
    public const string ChatTitle = "New message";

    // Contract 3.6: a chat push older than this is never delivered.
    public static readonly TimeSpan ChatPushLife = TimeSpan.FromHours(1);

    private static string N(Guid id) => id.ToString("N");

    private static string Args(params (string Key, string Value)[] pairs) => JsonSerializer.Serialize(pairs.ToDictionary(p => p.Key, p => p.Value));

    private static string Name(string? name) => string.IsNullOrWhiteSpace(name) ? "Someone" : name.Trim();

    public static PushSpec FollowRequest(Guid toUserId, Guid fromUserId, string? fromName) => new(
        toUserId, NotificationTypes.SocialFollowRequest, $"folreq:{N(fromUserId)}:{N(toUserId)}",
        "Follow request", $"{Name(fromName)} wants to follow you",
        UserNotificationRoutes.People, Args(("tab", "requests")), $"folreq:{N(toUserId)}");

    // One per receiver per hour.
    public static PushSpec NewFollower(Guid toUserId, Guid fromUserId, string? fromName, DateTime nowUtc) => new(
        toUserId, NotificationTypes.SocialNewFollower, $"fol:{N(toUserId)}:{BuddyPushCopy.Bucket(nowUtc, 60)}",
        "New follower", $"{Name(fromName)} started following you",
        UserNotificationRoutes.Traveller, Args(("userId", fromUserId.ToString())), $"fol:{N(toUserId)}");

    public static PushSpec FollowAccepted(Guid toUserId, Guid accepterId, string? accepterName) => new(
        toUserId, NotificationTypes.SocialFollowAccepted, $"folacc:{N(accepterId)}:{N(toUserId)}",
        "Request accepted", $"{Name(accepterName)} accepted your follow request",
        UserNotificationRoutes.Traveller, Args(("userId", accepterId.ToString())), $"folacc:{N(toUserId)}");

    public static PushSpec JoinRequest(Guid ownerId, Guid requestId, Guid tripId, string? requesterName) => new(
        ownerId, NotificationTypes.SocialJoinRequest, $"joinreq:{N(requestId)}",
        "Join request", $"{Name(requesterName)} asked to join your trip",
        UserNotificationRoutes.JoinRequests, Args(("tripId", tripId.ToString())), $"joinreq:{N(tripId)}");

    public static PushSpec JoinAccepted(Guid requesterId, Guid requestId, Guid tripId, string? ownerName) => new(
        requesterId, NotificationTypes.SocialJoinAccepted, $"joinacc:{N(requestId)}",
        "You are in", $"{Name(ownerName)} accepted your request",
        UserNotificationRoutes.TripView, Args(("tripId", tripId.ToString())), $"joinacc:{N(tripId)}");

    // One per room per receiver per minute; the tag collapses the room's pushes on the phone.
    public static PushSpec DirectMessage(Guid toUserId, Guid roomId, string? senderName, DateTime nowUtc) => new(
        toUserId, NotificationTypes.SocialChatMessage, $"chat:{N(roomId)}:{N(toUserId)}:{BuddyPushCopy.Bucket(nowUtc, 1)}",
        ChatTitle, $"{Name(senderName)} sent you a message",
        UserNotificationRoutes.DmRoom, Args(("roomId", roomId.ToString())), $"dm:{N(roomId)}", Expires: ChatPushLife);

    public static PushSpec TripMessage(Guid toUserId, Guid roomId, DateTime nowUtc) => new(
        toUserId, NotificationTypes.SocialTripChatMessage, $"chat:{N(roomId)}:{N(toUserId)}:{BuddyPushCopy.Bucket(nowUtc, 1)}",
        ChatTitle, "New message in your trip chat",
        UserNotificationRoutes.DmRoom, Args(("roomId", roomId.ToString())), $"dm:{N(roomId)}", Expires: ChatPushLife);
}
