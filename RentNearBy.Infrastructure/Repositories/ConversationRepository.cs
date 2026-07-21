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

    public async Task<int> RecomputeUnreadCountAsync(Guid conversationId, Guid readerId, bool readerIsRenter)
    {
        if (readerIsRenter)
        {
            await _context.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s.SetProperty(
                c => c.UnreadCountForRenter,
                c => _context.Messages.Count(m => m.ConversationId == conversationId && m.SenderId != readerId && m.ReadAt == null)));
        }
        else
        {
            await _context.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s.SetProperty(
                c => c.UnreadCountForOwner,
                c => _context.Messages.Count(m => m.ConversationId == conversationId && m.SenderId != readerId && m.ReadAt == null)));
        }

        return await _context.Conversations.Where(c => c.Id == conversationId)
            .Select(c => readerIsRenter ? c.UnreadCountForRenter : c.UnreadCountForOwner)
            .FirstAsync();
    }

    public async Task<int> ApplyIncomingMessageAsync(Guid conversationId, DateTime lastMessageAt, string lastMessagePreview, bool recipientIsRenter)
    {
        if (recipientIsRenter)
        {
            await _context.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LastMessageAt, lastMessageAt)
                .SetProperty(c => c.LastMessagePreview, lastMessagePreview)
                .SetProperty(c => c.UnreadCountForRenter, c => c.UnreadCountForRenter + 1));
        }
        else
        {
            await _context.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LastMessageAt, lastMessageAt)
                .SetProperty(c => c.LastMessagePreview, lastMessagePreview)
                .SetProperty(c => c.UnreadCountForOwner, c => c.UnreadCountForOwner + 1));
        }

        return await _context.Conversations.Where(c => c.Id == conversationId)
            .Select(c => recipientIsRenter ? c.UnreadCountForRenter : c.UnreadCountForOwner)
            .FirstAsync();
    }

    public async Task<int> GetTotalUnreadForUserAsync(Guid userId)
        => await _context.Conversations
            .Where(c => c.RenterId == userId || c.OwnerId == userId)
            .SumAsync(c => c.RenterId == userId ? c.UnreadCountForRenter : c.UnreadCountForOwner);
}
