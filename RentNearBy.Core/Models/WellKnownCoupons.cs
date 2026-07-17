namespace RentNearBy.Core.Models;

public static class WellKnownCoupons
{
    // Fixed id so the signup hook can redeem-by-id directly, without a code lookup.
    public static readonly Guid WelcomeSignupCouponId = new("00000000-0000-0000-0000-000000000001");

    public const string ManualCodeTrigger = "MANUAL_CODE";
    public const string WelcomeSignupTrigger = "WELCOME_SIGNUP";

    // Reserved for a future referral feature — no redemption path exists yet, and the admin coupon
    // form deliberately rejects creating a coupon with this trigger until one does.
    public const string ReferralTrigger = "REFERRAL";
}

public static class CouponStatuses
{
    public const string Active = "Active";
    public const string Exhausted = "Exhausted";
    public const string Expired = "Expired";
    public const string Revoked = "Revoked";
}
