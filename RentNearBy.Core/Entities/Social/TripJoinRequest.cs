namespace RentNearBy.Core.Entities.Social;

// Table "TripJoinRequests". Tier 0 = requester is in the owner's My Buddies, 1 = requester follows the owner, 2 = other.
public class TripJoinRequest
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid RequesterId { get; set; }
    public Guid ClientRequestId { get; set; }
    public string? Note { get; set; }
    public short Tier { get; set; }
    public string Status { get; set; } = "Waiting";
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}
