namespace RentNearBy.Core.Models;

// Plan feature keys are generic limits; every rule reads these, never a plan name or code.
public static class PlanFeatureKeys
{
    public const string ListingLimit = "ListingLimit";
    public const string Priority = "Priority";
    public const string Featured = "Featured";
    public const string JobsPosts = "JobsPosts";

    public static readonly string[] All = [ListingLimit, Priority, Featured, JobsPosts];
}

// Plans are scoped by listing kind; a role's feature is UserRoles.ListingKindOf(roleId).
public static class PlanFeatures
{
    public static readonly string[] All = [ListingKinds.Room, ListingKinds.Plot, ListingKinds.Stay, ListingKinds.Taxi];

    public static bool IsValid(string? featureKey) => featureKey != null && All.Contains(featureKey);

    // Rooms/Plots count listings against the plan limit; Stay/Taxi keep one row per account.
    public static bool HasListingSlots(string? featureKey) => featureKey is ListingKinds.Room or ListingKinds.Plot;
}

public static class PlanCycles
{
    public static readonly short[] All = [1, 3, 12];

    public static bool IsValid(int months) => months is 1 or 3 or 12;

    public static int DaysOf(int months) => months switch
    {
        1 => 30,
        3 => 90,
        12 => 365,
        _ => months * 30,
    };
}

public static class PlanRules
{
    public const int MaxRemainingDays = 730;
    public const int MaxTrialDays = 30;
    public const int MaxGrantDays = 730;
    public static readonly TimeSpan OpenOrderTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ReminderLead = TimeSpan.FromDays(7);
    public const int LowPriceWarningInr = 10;
    public const int NewUserWindowDays = 30;
    public const int MaxCouponCodeLength = 32;
}

public static class UserPlanSources
{
    public const string Prepaid = "Prepaid";
    public const string Subscription = "Subscription";
    public const string AdminGrant = "AdminGrant";
    public const string Trial = "Trial";
}

public static class UserPlanStatuses
{
    public const string Active = "Active";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
}

public static class PurchaseIntents
{
    public const string New = "New";
    public const string Renew = "Renew";
    public const string Switch = "Switch";
    public const string AdminGrant = "AdminGrant";
}

public static class PurchaseStatuses
{
    public const string Created = "Created";
    public const string Pending = "Pending";
    public const string Paid = "Paid";
    public const string Granted = "Granted";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
    public const string Refunded = "Refunded";

    public static readonly string[] Open = [Created, Pending];
}

public static class PurchaseSources
{
    public const string Verify = "Verify";
    public const string Webhook = "Webhook";
    public const string Reconcile = "Reconcile";
    public const string AdminRecheck = "AdminRecheck";
}

public static class GrantBlockedReasons
{
    public const string AmountMismatch = "AmountMismatch";
    public const string RoleMismatch = "RoleMismatch";
    public const string QuoteExpired = "QuoteExpired";
}

public static class PaymentProviders
{
    public const string Razorpay = "Razorpay";
    public const string None = "None";
}

public static class CouponKinds
{
    public const string Percent = "Percent";
    public const string Flat = "Flat";
    public const string FreePlan = "FreePlan";

    public static readonly string[] All = [Percent, Flat, FreePlan];
}

public static class CouponStatuses
{
    public const string Active = "Active";
    public const string Revoked = "Revoked";
    public const string Exhausted = "Exhausted";
}

public static class CouponRedemptionStatuses
{
    public const string Reserved = "Reserved";
    public const string Consumed = "Consumed";
    public const string Released = "Released";
}

// Why a room/plot row is not live. IsActive = false always carries one; every activation clears it.
public static class PauseReasons
{
    public const string Owner = "Owner";
    public const string Plan = "Plan";
    public const string Admin = "Admin";
    public const string Suspended = "Suspended";
}

// ApiError.Type values for the plan surface.
public static class PlanErrorTypes
{
    public const string PlanNotFound = "PlanNotFound";
    public const string PlanDisabled = "PlanDisabled";
    public const string PlanPriceNotFound = "PlanPriceNotFound";
    public const string RoleMismatch = "RoleMismatch";
    public const string TabInactive = "TabInactive";
    public const string PaymentDisabled = "PaymentDisabled";
    public const string PlanLimitReached = "PlanLimitReached";
    public const string ListingLimitReached = "ListingLimitReached";
    public const string CouponInvalid = "CouponInvalid";
    public const string CouponLocked = "CouponLocked";
    public const string PurchaseNotFound = "PurchaseNotFound";
    public const string SignatureInvalid = "SignatureInvalid";
    public const string AlreadyProcessed = "AlreadyProcessed";
    public const string TrialNotAvailable = "TrialNotAvailable";
    public const string SwapInvalid = "SwapInvalid";
    public const string ListingPausedByAdmin = "ListingPausedByAdmin";
    public const string RefundNotAllowed = "RefundNotAllowed";
    public const string PurchaseInProgress = "PurchaseInProgress";
    public const string GatewayError = "GatewayError";
    public const string RenewLimitReached = "RenewLimitReached";
    public const string InvalidRequest = "InvalidRequest";
    public const string UserPlanNotFound = "UserPlanNotFound";
}
