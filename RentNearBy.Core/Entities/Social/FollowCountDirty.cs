namespace RentNearBy.Core.Entities.Social;

// Table "FollowCountDirty": users whose FollowerCount / FollowingCount FollowEffectsJob must recount.
public class FollowCountDirty
{
    public Guid UserId { get; set; }
    public DateTime MarkedAt { get; set; }
}
