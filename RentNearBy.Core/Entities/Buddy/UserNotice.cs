namespace RentNearBy.Core.Entities.Buddy;

// Stored notification of this feature: ids, a kind and numbers, never text. One row per (user, kind, trip), upserted in place.
public class UserNotice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid TripId { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? RefId { get; set; }
    public int? Amount { get; set; }
    public int Count { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}
