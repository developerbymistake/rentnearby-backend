namespace RentNearBy.Core.Entities;

// Plan discount coupon (Percent | Flat | FreePlan). Code is stored upper-case.
public class Coupon
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty; // CouponKinds.*
    public int Value { get; set; }
    public int? MaxDiscountInr { get; set; }
    public int? MinPlanPriceInr { get; set; }
    public string? FeatureKey { get; set; } // null = any feature
    public Guid[]? AllowedPlanIds { get; set; }
    public bool NewUsersOnly { get; set; }
    public bool FirstPurchaseOnly { get; set; }
    public int PerUserLimit { get; set; } = 1;
    public int? MaxTotalRedemptions { get; set; }
    public int RedemptionCount { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public bool AutoApply { get; set; }
    public string Status { get; set; } = string.Empty; // CouponStatuses.*
    public string? CampaignLabel { get; set; }
    public Guid? CreatedByAdminId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
