namespace RentNearBy.Core.DTOs.Requests;

// Full replace. Kind, Value and scope (FeatureKey, AllowedPlanIds) are locked once RedemptionCount > 0.
public record UpdateCouponRequest(
    string Kind,
    int Value,
    int? MaxDiscountInr,
    int? MinPlanPriceInr,
    string? FeatureKey,
    List<Guid>? AllowedPlanIds,
    bool NewUsersOnly,
    bool FirstPurchaseOnly,
    int PerUserLimit,
    int? MaxTotalRedemptions,
    DateTime? ValidFrom,
    DateTime? ValidUntil,
    bool AutoApply,
    string? CampaignLabel);
