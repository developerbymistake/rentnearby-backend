namespace RentNearBy.Core.Entities;

// Price/OriginalPrice/DiscountPercent copied field-for-field from CoinPlan's strikethrough +
// "X% Savings" badge logic. Price=null renders "Get Custom Quote" (Insurance, Financial Planning).
// Price set + DiscountPercent>0 renders the discount badge (Tourism packages).
public class ServicePackage
{
    public Guid Id { get; set; }
    public Guid ServiceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Price { get; set; }
    public int? OriginalPrice { get; set; }
    public int? DiscountPercent { get; set; }

    // When true AND Price is set, render "Starting at ₹X" instead of a bare "₹X" (Tour & Travel
    // Packages specifically). Compatible with the discount fields being set simultaneously — a
    // "Starting at ₹8,999" package can still show a strikethrough OriginalPrice.
    public bool IsStartingAtPrice { get; set; } = false;

    public int? DurationDays { get; set; }
    public int? DurationNights { get; set; }
    public string? PriceUnit { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ThumbnailFilePath { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsFeatured { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Service Service { get; set; } = null!;
    public ICollection<PackageInclusion> PackageInclusions { get; set; } = new List<PackageInclusion>();
}
