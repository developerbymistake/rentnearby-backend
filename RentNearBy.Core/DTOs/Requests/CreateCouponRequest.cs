namespace RentNearBy.Core.DTOs.Requests;

public record CreateCouponRequest(
    string Code,
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
