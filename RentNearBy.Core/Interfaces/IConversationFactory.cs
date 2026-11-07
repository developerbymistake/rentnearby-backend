using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public sealed record ConversationFactoryResult(
    Conversation? Conversation, bool Created, int Status, string? Message, string? ErrorType, int? RetryAfterSeconds = null)
{
    public bool IsSuccess => Conversation != null && Status is 200 or 201;
}

public interface IConversationFactory
{
    // Same gates as ChatHandlers.CreateConversation: tab, caller exists, new-account cooldown, listing lookup (Stay),
    // own listing, blocks both ways, FindExisting (+ lifecycle refresh), rate limit chat:newconv (skipped when skipRateLimit),
    // insert with race re-read. requireActive: a listing that is not live answers 409 ListingNotAvailable instead of creating
    // a ListingInactive conversation.
    Task<ConversationFactoryResult> GetOrCreateAsync(
        Guid callerId, string listingType, Guid listingId, bool skipRateLimit = false, bool requireActive = true,
        bool skipNewAccountCooldown = false);
}
