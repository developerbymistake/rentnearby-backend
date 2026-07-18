namespace RentNearBy.Core.DTOs.Responses;

// Lightweight package shape embedded in ServiceDetailDto's package-preview list — full ServicePackageDto
// (with Inclusions) is fetched separately once the consumer drills into the Package List screen.
public class ServicePackagePreviewDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Price { get; set; }
    public int? OriginalPrice { get; set; }
    public int? DiscountPercent { get; set; }
    public bool IsStartingAtPrice { get; set; }
    public int? DurationDays { get; set; }
    public int? DurationNights { get; set; }
    public string? PriceUnit { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public bool IsFeatured { get; set; }
    public int SortOrder { get; set; }
}
