namespace RentNearBy.Core.Entities;

public class DistrictBanner
{
    public Guid Id { get; set; }
    public Guid DistrictId { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public string ImageFilePath { get; set; } = string.Empty;
    public string? ContactNumber { get; set; }
    public string? RedirectUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public District District { get; set; } = null!;
    public ICollection<BannerDismissal> Dismissals { get; set; } = new List<BannerDismissal>();
}
