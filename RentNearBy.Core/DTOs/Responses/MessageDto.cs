namespace RentNearBy.Core.DTOs.Responses;

// Constructed exclusively via MessageDtoExtensions.ToDto() (RentNearBy.Api/Extensions) — every
// property required so a future hand-rolled `new MessageDto {...}` elsewhere fails to compile
// instead of silently shipping an incomplete DTO (this repo has no automated tests to catch that
// at runtime).
public class MessageDto
{
    public required Guid Id { get; set; }
    public required Guid ConversationId { get; set; }
    public required Guid SenderId { get; set; }
    public required bool IsMine { get; set; }
    public required string Type { get; set; }
    public required string PayloadJson { get; set; }
    public required Guid? RespondsToMessageId { get; set; }
    public required DateTime? ReadAt { get; set; }
    public required DateTime CreatedAt { get; set; }
}
