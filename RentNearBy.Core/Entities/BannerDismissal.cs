namespace RentNearBy.Core.Entities;

public class BannerDismissal
{
    public Guid UserId { get; set; }
    public Guid BannerId { get; set; }
    public DateTime DismissedAt { get; set; }

    public User User { get; set; } = null!;
    public DistrictBanner Banner { get; set; } = null!;
}
