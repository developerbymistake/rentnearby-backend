namespace RentNearBy.Core.Entities.Social;

// Table "FollowPushGroups": one open grouped follow push per receiver and kind (A = followed you, R = asked to follow you).
public class FollowPushGroup
{
    public Guid ReceiverId { get; set; }
    public string Kind { get; set; } = "A";
    public DateTime? PendingSince { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? LastSentAt { get; set; }
}
