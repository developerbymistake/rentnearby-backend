using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Api.Extensions;

// The single place a Message entity becomes a MessageDto — every field, every call site,
// forever. ChatHandlers.cs used to hand-construct this DTO at 11 separate sites; several
// independently forgot RespondsToMessageId/ReadAt, which silently broke the Flutter client's
// "has this already been answered" detection (it scans for a message whose respondsToMessageId
// matches). isMine stays a parameter, not derived here, since its value depends on who's asking.
public static class MessageDtoExtensions
{
    public static MessageDto ToDto(this Message m, bool isMine) => new()
    {
        Id = m.Id,
        ConversationId = m.ConversationId,
        SenderId = m.SenderId,
        IsMine = isMine,
        Type = m.Type,
        PayloadJson = m.PayloadJson,
        RespondsToMessageId = m.RespondsToMessageId,
        ReadAt = m.ReadAt,
        CreatedAt = m.CreatedAt,
    };
}
