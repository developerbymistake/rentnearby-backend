using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IMessageRepository : IRepository<Message>
{
    Task<IReadOnlyList<Message>> GetPagedForConversationAsync(Guid conversationId, DateTime? beforeCreatedAt, DateTime? afterCreatedAt, int limit);
    Task<int> MarkReadBulkAsync(Guid conversationId, Guid readerUserId);
}
