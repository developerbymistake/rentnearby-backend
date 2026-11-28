namespace RentNearBy.Core.Entities.Social;

// Table "DiscoverHides": X on a Discover card hides that person from the user's suggestions for 30 days (purged after).
public class DiscoverHide
{
    public Guid UserId { get; set; }
    public Guid HiddenUserId { get; set; }
    public DateTime HiddenAt { get; set; }
}
