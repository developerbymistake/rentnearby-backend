namespace RentNearBy.Core.Entities.Social;

// Table "ChatRooms". Kind D = direct (pair), T = trip group. Seq numbers are per room.
public class ChatRoom
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "D";
    public Guid? PairLowId { get; set; }
    public Guid? PairHighId { get; set; }
    public Guid? TripId { get; set; }
    public string Status { get; set; } = "Active";
    public Guid? RequestedByUserId { get; set; }
    public DateTime? ChargedAt { get; set; }
    public DateTime? DeclinedAt { get; set; }
    public DateTime? BlockedAt { get; set; }
    public long LastSeq { get; set; }
    public string? LastPreview { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
