namespace RentNearBy.Core.Entities;

// Per-user read receipt — created lazily only when a user actually reads a NotificationEvent, never
// pre-populated for every potential recipient. Composite key (UserId, NotificationId): a broadcast
// read by 3 of 100,000 targeted users costs exactly 3 rows here, not 100,000.
public class NotificationRead
{
    public Guid UserId { get; set; }
    public Guid NotificationId { get; set; }
    public DateTime ReadAt { get; set; }

    public User User { get; set; } = null!;
    public NotificationEvent Notification { get; set; } = null!;
}
