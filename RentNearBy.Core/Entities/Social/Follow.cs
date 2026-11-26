namespace RentNearBy.Core.Entities.Social;

// Table "Follows". Status A = accepted, R = requested (private target).
public class Follow
{
    public Guid FollowerId { get; set; }
    public Guid FolloweeId { get; set; }
    public string Status { get; set; } = "A";
    public DateTime CreatedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
}
