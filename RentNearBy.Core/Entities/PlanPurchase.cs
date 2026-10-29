namespace RentNearBy.Core.Entities;

// One row per charge/order (or admin grant). Kept after account deletion with UserId nulled.
public class PlanPurchase
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string FeatureKey { get; set; } = string.Empty;
    public Guid PlanId { get; set; }
    public string PlanNameSnapshot { get; set; } = string.Empty;
    public short? CycleMonths { get; set; }
    public int GrantedDays { get; set; }
    public string Intent { get; set; } = string.Empty;   // PurchaseIntents.*
    public string Provider { get; set; } = string.Empty; // PaymentProviders.*
    public string Currency { get; set; } = "INR";
    public int ListPriceInr { get; set; }
    public int DiscountInr { get; set; }
    public int CreditInr { get; set; }
    public int PayableInr { get; set; }
    public Guid? CouponId { get; set; }
    public string? CouponCodeSnapshot { get; set; }
    public string Status { get; set; } = string.Empty;   // PurchaseStatuses.*
    public string? RazorpayOrderId { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public long? CapturedAmountPaise { get; set; }
    public string? RazorpayRefundId { get; set; }
    public string? GrantBlockedReason { get; set; }
    public string? FailureReason { get; set; }
    public string? FailedPaymentId { get; set; }
    public Guid? GrantedUserPlanId { get; set; }
    public string? ClientIdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? GrantedAt { get; set; }
    public DateTime? RefundedAt { get; set; }
    public DateTime? AnonymisedAt { get; set; }
    public Guid? GrantedByAdminId { get; set; }
}
