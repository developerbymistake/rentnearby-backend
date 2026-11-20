using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Entities.Buddy;

public class BuddyRequest
{
    public Guid Id { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public Guid PairLowId { get; set; }
    public Guid PairHighId { get; set; }
    public string Status { get; set; } = BuddyRequestStatuses.Pending;
    public string Source { get; set; } = BuddyRequestSources.Search;
    public Guid ClientRequestId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public bool BlockedSender { get; set; }
}
