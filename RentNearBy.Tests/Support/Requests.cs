using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Support;

// Fully valid request samples; tests change one field with `with { ... }`.
public static class Requests
{
    public static UpsertPlanRequest UpsertPlan() => new(
        FeatureKey: ListingKinds.Room,
        Code: "PLUS",
        Name: "Plus",
        Tagline: "More listings",
        IsEnabled: true,
        TrialDays: 7,
        SortOrder: 1,
        Features:
        [
            new PlanFeatureInput(PlanFeatureKeys.ListingLimit, 3),
            new PlanFeatureInput(PlanFeatureKeys.Priority, 2),
            new PlanFeatureInput(PlanFeatureKeys.Featured, 1),
        ],
        Prices:
        [
            new PlanPriceInput(1, 299, null, true, "cycle-1m", "trial-7d"),
            new PlanPriceInput(3, 799, null, true, "cycle-3m"),
            new PlanPriceInput(12, 2999, null, true, "cycle-12m"),
        ],
        PlayProductId: "room_plus");

    public static CreateCouponRequest CreateCoupon() => new(
        Code: "SAVE10", Kind: CouponKinds.Percent, Value: 10, MaxDiscountInr: null, MinPlanPriceInr: null,
        FeatureKey: null, AllowedPlanIds: null, NewUsersOnly: false, FirstPurchaseOnly: false, PerUserLimit: 1,
        MaxTotalRedemptions: null, ValidFrom: null, ValidUntil: null, AutoApply: false, CampaignLabel: null);

    public static UpdateCouponRequest UpdateCoupon() => new(
        Kind: CouponKinds.Percent, Value: 10, MaxDiscountInr: null, MinPlanPriceInr: null, FeatureKey: null,
        AllowedPlanIds: null, NewUsersOnly: false, FirstPurchaseOnly: false, PerUserLimit: 1, MaxTotalRedemptions: null,
        ValidFrom: null, ValidUntil: null, AutoApply: false, CampaignLabel: null);

    public static CouponRuleInput CouponRule() => new(
        Kind: CouponKinds.Percent, Value: 10, MaxDiscountInr: null, MinPlanPriceInr: null, FeatureKey: null, PerUserLimit: 1,
        MaxTotalRedemptions: null, ValidFrom: null, ValidUntil: null, AutoApply: false, CampaignLabel: null);

    public static UpdatePaymentFeatureRequest UpdatePaymentFeature() => new(IsEnabled: true, FreeDurationDays: null, Reason: "test");

    public static string Token(int length) => new('t', length);
}
