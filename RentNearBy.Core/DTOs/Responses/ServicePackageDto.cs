namespace RentNearBy.Core.DTOs.Responses;

public class ServicePackageDto
{
    public Guid Id { get; set; }
    public Guid ServiceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Price { get; set; }
    public int? OriginalPrice { get; set; }
    public int? DiscountPercent { get; set; }
    public bool IsStartingAtPrice { get; set; }
    public int? DurationDays { get; set; }
    public int? DurationNights { get; set; }
    public string? PriceUnit { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<InclusionDto> Inclusions { get; set; } = new();
}
