using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public sealed record TripChatPost(Conversation Conversation, Message Message, int RecipientUnread, bool IsNew);

public interface ITripChatService
{
    // Row + preview + unread bump inside the caller's transaction; no broadcast.
    Task<TripChatPost> PostAsync(Conversation c, Guid senderId, string type, object payload, string preview);
    Task<TripChatPost> PostSystemAsync(Conversation c, Guid actorUserId, string kind, string text);
    // Sets payload.status in place; null when the message is gone.
    Task<TripChatPost?> RewriteStatusAsync(Guid messageId, string status);
    // Newest card of a type for an enquiry (payload.enquiryId); null when none.
    Task<Guid?> FindCardMessageIdAsync(Guid conversationId, string type, Guid enquiryId);
    // MessageReceived / MessageUpdated + UnreadCountChanged; never throws.
    Task AfterCommitAsync(IEnumerable<TripChatPost> posts);
}
