using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IConversationRepository : IRepository<Conversation>
{
    Task<IReadOnlyList<Conversation>> GetForUserPagedAsync(Guid userId, int offset, int limit);
    Task<Conversation?> FindExistingAsync(Guid renterId, Guid ownerId, string listingType, Guid listingId);
}
