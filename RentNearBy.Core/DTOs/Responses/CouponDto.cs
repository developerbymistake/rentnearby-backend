namespace RentNearBy.Core.DTOs.Responses;

public class CouponDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Value { get; set; }
    public int? MaxDiscountInr { get; set; }
    public int? MinPlanPriceInr { get; set; }
    public string? FeatureKey { get; set; }
    public List<Guid>? AllowedPlanIds { get; set; }
    public bool NewUsersOnly { get; set; }
    public bool FirstPurchaseOnly { get; set; }
    public int PerUserLimit { get; set; }
    public int? MaxTotalRedemptions { get; set; }
    public int RedemptionCount { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public bool AutoApply { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CampaignLabel { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CouponRedemptionDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string? UserPhoneMasked { get; set; }
    public Guid PurchaseId { get; set; }
    public string? PlanName { get; set; }
    public int DiscountInr { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
