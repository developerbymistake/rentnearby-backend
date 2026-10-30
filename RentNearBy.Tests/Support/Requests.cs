using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Support;

// Fully valid request samples; tests change one field with `with { ... }` (records) or by setting it (classes).
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

    // Mutable classes (no `with`): tests take a fresh sample and set one property.
    public static UpsertTaxiBasicRequest UpsertTaxiBasic() => new()
    {
        VehicleTypeId = Guid.Parse("a1000000-0000-0000-0000-000000000001"),
        Seats = 4,
        NumberPlate = "UK 07 TA 1234",
        ContactPhone = "9876543210",
        WhatsAppNumber = "9876543210",
        Latitude = 30.3165m,
        Longitude = 78.0322m,
        DistrictId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
        DrivingLicenceNumber = "UK07 20150012345",
        DateOfBirth = "1990-05-14",
    };

    public static UpsertStayBasicRequest UpsertStayBasic() => new()
    {
        Name = "Hill View Homestay",
        StayTypeId = Guid.Parse("e1000000-0000-0000-0000-000000000001"),
        VehicleAccess = StayVehicleAccess.CarToGate,
        HostLivesHere = true,
        Facilities = [StayFacilities.HotWater],
        Latitude = 30.3165m,
        Longitude = 78.0322m,
        Address = "Mall Road, Mussoorie",
        DistrictId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
        RegistrationTypeId = Guid.Parse("e2000000-0000-0000-0000-000000000004"),
        RegistrationNumber = "UDYAM-UK-05-0001234",
        Gstin = "05ABCDE1234F1Z6",
    };

    public static string Token(int length) => new('t', length);
}
