namespace RentNearBy.Core.Entities;

// Basic/Standard/Premium tiers for a CoinFeature. Days and Quota are never optional — every plan
// genuinely has both a validity window and a resource quantity; only the quantity's unit (rooms,
// plots, and eventually contacts/conversations/etc.) is defined by the owning CoinFeature.
public class CoinPlan
{
    public Guid Id { get; set; }
    public string FeatureKey { get; set; } = string.Empty; // CoinFeature.Key
    public string PlanType { get; set; } = string.Empty;   // "BASIC" | "STANDARD" | "PREMIUM"
    public int Days { get; set; }
    public int Quota { get; set; }
    public int Price { get; set; }
    public int DiscountPercent { get; set; } = 0;
    public int OriginalPrice { get; set; } = 0;
    public bool IsFeatured { get; set; } = false;
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
