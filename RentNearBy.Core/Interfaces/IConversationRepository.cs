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
}
