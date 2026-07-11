using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ConversationRepository : Repository<Conversation>, IConversationRepository
{
    public ConversationRepository(ApplicationDbContext context) : base(context) { }

    public async Task<IReadOnlyList<Conversation>> GetForUserPagedAsync(Guid userId, int offset, int limit)
        => await _context.Conversations
            .Where(c => c.RenterId == userId || c.OwnerId == userId)
            .OrderByDescending(c => c.LastMessageAt)
            .Skip(offset)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync();

    public async Task<Conversation?> FindExistingAsync(Guid renterId, Guid ownerId, string listingType, Guid listingId)
        => await _context.Conversations
            .FirstOrDefaultAsync(c =>
                c.RenterId == renterId && c.OwnerId == ownerId &&
                c.ListingType == listingType && c.ListingId == listingId);

    public async Task<IReadOnlyList<Conversation>> GetAllBetweenUsersAsync(Guid userA, Guid userB)
        => await _context.Conversations
            .Where(c =>
                (c.RenterId == userA && c.OwnerId == userB) ||
                (c.RenterId == userB && c.OwnerId == userA))
            .ToListAsync();
}
