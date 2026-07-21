using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IConversationRepository : IRepository<Conversation>
{
    Task<IReadOnlyList<Conversation>> GetForUserPagedAsync(Guid userId, int offset, int limit);
    Task<Conversation?> FindExistingAsync(Guid renterId, Guid ownerId, string listingType, Guid listingId);

    // Blocking is between two people, not scoped to one listing — this finds every
    // conversation between the pair (in either renter/owner direction) so a block
    // can be applied everywhere at once.
    Task<IReadOnlyList<Conversation>> GetAllBetweenUsersAsync(Guid userA, Guid userB);

    // Atomic, single-statement recompute of one side's unread counter from the actual
    // Messages.ReadAt source of truth — never a read-then-write "set to 0", which a
    // concurrent incoming message could silently clobber. Returns the fresh count so the
    // caller can push it live without a second round-trip.
    Task<int> RecomputeUnreadCountAsync(Guid conversationId, Guid readerId, bool readerIsRenter);

    // Atomic bump of the recipient's unread counter + last-message preview together, in one
    // ExecuteUpdateAsync — replaces the old tracked-entity mutation that let concurrent writers
    // to the same Conversation row (message send, mark-read, block/unblock) clobber each
    // other's changes on every field, not just the counter. Returns the recipient's fresh
    // unread count for the caller to broadcast.
    Task<int> ApplyIncomingMessageAsync(Guid conversationId, DateTime lastMessageAt, string lastMessagePreview, bool recipientIsRenter);

    // Total unread across every conversation the user is a party to, resolved to their side
    // per row — the single cheap SUM the client's badge anchors to (GET /chat/unread-count).
    // The client's paginated conversations list can't compute this itself without loading
    // every page, which is exactly the under-count this exists to avoid.
    Task<int> GetTotalUnreadForUserAsync(Guid userId);
}
