namespace RentNearBy.Core.Entities.Social;

// Table "ChatMessages". SenderId null = system line (SystemKind joined/left). Body is the masked text only.
public class ChatMessage
{
    public Guid RoomId { get; set; }
    public long Seq { get; set; }
    public Guid? SenderId { get; set; }
    public string? SystemKind { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? ClientMessageId { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool Masked { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
