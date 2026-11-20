namespace RentNearBy.Core.Entities.Buddy;

// Table "Buddies", two rows per pair. Not named Buddy: that clashes with the Buddy namespaces.
public class BuddyLink
{
    public Guid UserId { get; set; }
    public Guid BuddyUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? RequestId { get; set; }
    public DateTime? SeenAt { get; set; }
}
