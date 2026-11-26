namespace RentNearBy.Core.Entities.Social;

// Table "MessageSnapshots": copy of a reported chat (max 50 messages), kept until PurgeAt.
public class MessageSnapshot
{
    public Guid Id { get; set; }
    public Guid? ReportId { get; set; }
    public Guid RoomId { get; set; }
    public Guid TakenForUserId { get; set; }
    public Guid AboutUserId { get; set; }
    public long? AnchorSeq { get; set; }
    public string MessagesJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public DateTime PurgeAt { get; set; }
}
