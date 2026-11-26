using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Interfaces.Social;

// One send / read core for the ChatHub methods (DmSend, DmRead) and their REST twins (contract 3.4).
public interface ISocialChatService
{
    Task<BuddyResult<ChatMessageDto>> SendAsync(Guid me, Guid roomId, Guid clientMessageId, string text, CancellationToken ct = default);

    Task<BuddyResult<ChatUnreadDto>> ReadAsync(Guid me, Guid roomId, long seq, CancellationToken ct = default);
}
