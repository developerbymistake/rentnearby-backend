namespace RentNearBy.Core.Entities;

// One row per Google Play purchase token (subscriptionsv2.get is the source of truth). Kept after account
// deletion with UserId nulled (financial record).
public class PlaySubscription
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string? FeatureKey { get; set; }
    public Guid? PlanId { get; set; }
    public Guid? PlanPriceId { get; set; }
    public string PurchaseToken { get; set; } = string.Empty;
    // The entitled line item's product (latest non-null expiryTime), not necessarily the purchased one.
    public string ProductId { get; set; } = string.Empty;
    // Product the token was bought for; the acknowledge call uses this one.
    public string PurchasedProductId { get; set; } = string.Empty;
    public string? DetachedReason { get; set; }          // PlayDetachReasons.*
    public string? BasePlanId { get; set; }
    public string? OfferId { get; set; }
    public string? LinkedPurchaseToken { get; set; }
    public string? ExpiredPurchaseToken { get; set; }
    public string MappingStatus { get; set; } = string.Empty; // PlayMappingStatuses.*
    public string State { get; set; } = string.Empty;         // PlayStates.*
    public bool AutoRenewEnabled { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? ExpiryTime { get; set; }
    public string? LatestOrderId { get; set; }
    public string AcknowledgementState { get; set; } = string.Empty; // PlayAckStates.*
    public DateTime? AcknowledgedAt { get; set; }
    public int AckAttempts { get; set; }
    public string? RegionCode { get; set; }
    public bool IsTestPurchase { get; set; }
    public string? ObfuscatedAccountId { get; set; }
    public string? CancelReason { get; set; }            // PlayCancelReasons.*
    public DateTime? PausedAutoResumeAt { get; set; }
    public string? DeferredProductId { get; set; }
    public long? RecurringPriceMicros { get; set; }
    public string? RecurringPriceCurrency { get; set; }
    public string? PriceChangeState { get; set; }
    public string? PriceChangeMode { get; set; }
    public int? LastRtdnType { get; set; }
    public long? LastEventTimeMillis { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public string? LastSyncError { get; set; }
    public string? LastSnapshotJson { get; set; }
    public string? PendingServerAction { get; set; }     // PlayServerActions.*
    public string? PendingActionReason { get; set; }
    public int PendingActionAttempts { get; set; }
    public DateTime? NextActionAt { get; set; }
    public DateTime? AnonymisedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
