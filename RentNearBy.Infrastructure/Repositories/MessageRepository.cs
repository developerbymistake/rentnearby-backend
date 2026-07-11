using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class MessageRepository : Repository<Message>, IMessageRepository
{
    public MessageRepository(ApplicationDbContext context) : base(context) { }

    public async Task<IReadOnlyList<Message>> GetPagedForConversationAsync(Guid conversationId, DateTime? beforeCreatedAt, int limit)
    {
        var query = _context.Messages.Where(m => m.ConversationId == conversationId);
        if (beforeCreatedAt.HasValue)
            query = query.Where(m => m.CreatedAt < beforeCreatedAt.Value);

        return await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync();
    }

    // Bulk update, not one row at a time — the recipient's chat screen calls this once on open.
    public async Task<int> MarkReadBulkAsync(Guid conversationId, Guid readerUserId)
        => await _context.Messages
            .Where(m => m.ConversationId == conversationId && m.SenderId != readerUserId && m.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, DateTime.UtcNow));
}
