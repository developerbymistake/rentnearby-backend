using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Core.Models;

// purchases.subscriptionsv2 subscriptionState values.
public static class PlayStates
{
    public const string Unspecified = "SUBSCRIPTION_STATE_UNSPECIFIED";
    public const string Pending = "SUBSCRIPTION_STATE_PENDING";
    public const string Active = "SUBSCRIPTION_STATE_ACTIVE";
    public const string Paused = "SUBSCRIPTION_STATE_PAUSED";
    public const string InGracePeriod = "SUBSCRIPTION_STATE_IN_GRACE_PERIOD";
    public const string OnHold = "SUBSCRIPTION_STATE_ON_HOLD";
    public const string Canceled = "SUBSCRIPTION_STATE_CANCELED";
    public const string Expired = "SUBSCRIPTION_STATE_EXPIRED";
    public const string PendingPurchaseCanceled = "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED";

    public static bool IsEntitled(string s, DateTime? expiry)
        => s is Active or InGracePeriod || (s is Canceled && expiry > DateTime.UtcNow);
}

public static class PlayAckStates
{
    public const string Pending = "ACKNOWLEDGEMENT_STATE_PENDING";
    public const string Acknowledged = "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED";
}

// subscriptionNotification.notificationType (there is no type 21).
public static class PlayRtdnTypes
{
    public const int Recovered = 1;
    public const int Renewed = 2;
    public const int Canceled = 3;
    public const int Purchased = 4;
    public const int OnHold = 5;
    public const int InGracePeriod = 6;
    public const int Restarted = 7;
    public const int PriceChangeConfirmed = 8;
    public const int Deferred = 9;
    public const int Paused = 10;
    public const int PauseScheduleChanged = 11;
    public const int Revoked = 12;
    public const int Expired = 13;
    public const int ItemsChanged = 17;
    public const int CancellationScheduled = 18;
    public const int PriceChangeUpdated = 19;
    public const int PendingPurchaseCanceled = 20;
    public const int PriceStepUpConsentUpdated = 22;
}

public static class PlayMappingStatuses
{
    public const string Mapped = "Mapped";
    public const string Unmapped = "Unmapped";
    public const string Rejected = "Rejected";
    public const string Detached = "Detached";
}

public static class PlayDetachReasons
{
    public const string Replaced = "Replaced";
    public const string RoleChanged = "RoleChanged";
    public const string AccountDeleted = "AccountDeleted";
    public const string AdminRevoked = "AdminRevoked";
}

// canceledStateContext union member names.
public static class PlayCancelReasons
{
    public const string User = "userInitiatedCancellation";
    public const string System = "systemInitiatedCancellation";
    public const string Developer = "developerInitiatedCancellation";
    public const string Replacement = "replacementCancellation";
}

public static class PlayServerActions
{
    public const string Cancel = "Cancel";
    public const string RevokeFull = "RevokeFull";
    public const string RevokeProrated = "RevokeProrated";

    public static readonly string[] All = [Cancel, RevokeFull, RevokeProrated];
}

// Sent on the wire as the Dart ReplacementMode enum names.
public static class PlayReplacementModes
{
    public const string ChargeFullPrice = "chargeFullPrice";
    public const string WithoutProration = "withoutProration";
    public const string Deferred = "deferred";
}

public static class PlayRules
{
    public static readonly TimeSpan AckDeadline = TimeSpan.FromDays(3);
    public static readonly TimeSpan StaleExpiryGrace = TimeSpan.FromHours(72);
    public static readonly TimeSpan StaleSyncAge = TimeSpan.FromHours(24);
    public const int MaxCarryOverDays = 730;
    public const int TokenTailLength = 8;
    public const int RestoreMaxTokens = 10;
    public const int ObfuscatedIdLength = 64;
    public static readonly TimeSpan EndedStateWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan PendingOrderTtl = TimeSpan.FromDays(3);
}

// Config section "PlayBilling". Missing config never crashes: Play endpoints answer 503 PlayNotConfigured.
public sealed class PlayBillingOptions
{
    public const string Section = "PlayBilling";

    public string PackageName { get; set; } = string.Empty;
    public string? ServiceAccountJson { get; set; }
    public string? ServiceAccountJsonPath { get; set; }
    public string RtdnAudience { get; set; } = string.Empty;
    public string RtdnServiceAccountEmail { get; set; } = string.Empty;
    // Never rotate after the first purchase: it keys every Users.PlayAccountKey.
    public string AccountIdSalt { get; set; } = string.Empty;
    // Role change and account deletion: PlayServerActions.RevokeProrated | PlayServerActions.Cancel.
    public string RoleChangeAction { get; set; } = PlayServerActions.Cancel;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(PackageName)
        && (!string.IsNullOrEmpty(ServiceAccountJson) || !string.IsNullOrEmpty(ServiceAccountJsonPath))
        && (AccountIdSalt?.Length ?? 0) >= 32;
}

public sealed record PlaySubscriptionLineItem(string ProductId, DateTime? ExpiryTime, string? BasePlanId, string? OfferId,
    bool? AutoRenewEnabled, bool IsPrepaid, string? LatestOrderId, string? DeferredProductId,
    long? RecurringPriceMicros, string? RecurringPriceCurrency, string? PriceChangeMode, string? PriceChangeState);

public sealed record PlaySubscriptionSnapshot(string PurchaseToken, string State, string AcknowledgementState, DateTime? StartTime,
    string? LinkedPurchaseToken, string? ExpiredPurchaseToken, string? RegionCode, bool IsTestPurchase,
    string? ObfuscatedAccountId, string? ObfuscatedProfileId, string? CancelReason, DateTime? PausedAutoResumeTime,
    IReadOnlyList<PlaySubscriptionLineItem> LineItems, string RawJson)
{
    // Line-item order is unverified: never index [0].
    public PlaySubscriptionLineItem? Entitled => LineItems.Where(l => l.ExpiryTime != null).OrderByDescending(l => l.ExpiryTime).FirstOrDefault();
    public PlaySubscriptionLineItem? DeferredTarget => LineItems.FirstOrDefault(l => l.ExpiryTime == null);
    public string? DeferredProductId => LineItems.Select(l => l.DeferredProductId).FirstOrDefault(p => p != null) ?? DeferredTarget?.ProductId;
    public DateTime? ExpiryTime => Entitled?.ExpiryTime;
    public PlaySubscriptionLineItem? Find(string productId) => LineItems.FirstOrDefault(l => l.ProductId == productId);
    public bool IsEntitled => PlayStates.IsEntitled(State, ExpiryTime);
}

public sealed record PlayVoidedPurchase(string PurchaseToken, string OrderId, DateTime VoidedAt, int VoidedSource, int VoidedReason);

public enum PlayApiErrorKind { NotFound, Invalid, PermissionDenied, Transient }

public sealed class PlayApiException(PlayApiErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public PlayApiErrorKind Kind { get; } = kind;
}

public enum PlayRefundKind { Full, Prorated }

public enum PlayCancelKind { UserRequestedStopRenewals, DeveloperRequestedStopPayments }

public sealed record PlaySyncRequest(string PurchaseToken, string Source, Guid? CallerUserId = null, Guid? PurchaseId = null,
    string? ClientProductId = null, int? RtdnType = null, long? EventTimeMillis = null, string? EventId = null);

public enum PlaySyncOutcome
{
    Granted,
    Renewed,
    Switched,
    Recovered,
    Unchanged,
    Pending,
    PendingCanceled,
    Grace,
    OnHold,
    Paused,
    Cancelled,
    Expired,
    Revoked,
    CarriedOver,
    Replaced,
    Unmapped,
    Rejected,
    Detached,
    ProductUnknown,
    Invalid,
    ApiError,
}

public sealed record PlaySyncResult(PlaySyncOutcome Outcome, Guid? PlaySubscriptionId, Guid? PurchaseId, Guid? UserPlanId, Guid? UserId,
    string? FeatureKey, string? BlockedReason, PlayApiErrorKind? ApiError, NotificationEvent? Notification, DateTime? ExpiryTime);

// Plan/Price = the ENTITLED line item's; ScheduledPlan = the deferred target's plan (R-DEFER).
public sealed record PlayResolvedTarget(Guid UserId, string FeatureKey, CatalogPlan Plan, CatalogPrice Price, string PlayAccountKey,
    CatalogPlan? ScheduledPlan, string PurchasedProductId, bool Reattach);

public enum PlayApplyKind { Granted, Renewed, Switched, Resubscribed, Recovered, Reattached, Unchanged }

public sealed record PlayApplyResult(PlayApplyKind Kind, Guid PlaySubscriptionId, Guid UserId, string FeatureKey, UserPlan UserPlan,
    PlanPurchase? ChargeRow, NotificationEvent? Notification, bool NeedsAcknowledge, string PurchasedProductId, string PurchaseToken);

public enum PlayRtdnOutcome { Processed, Duplicate, Ignored, Unmapped, Rejected, Invalid }
