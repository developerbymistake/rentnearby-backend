namespace RentNearBy.Core.DTOs.Responses;

public class AdminPurchaseDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string? UserPhoneMasked { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string FeatureKey { get; set; } = string.Empty;
    public string Intent { get; set; } = string.Empty;
    public int? CycleMonths { get; set; }
    public int GrantedDays { get; set; }
    public int ListPriceInr { get; set; }
    public int DiscountInr { get; set; }
    public int CreditInr { get; set; }
    public int PayableInr { get; set; }
    public string? CouponCode { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string? RazorpayOrderId { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public string? RazorpayRefundId { get; set; }
    public string? GrantBlockedReason { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? GrantedAt { get; set; }
    public DateTime? RefundedAt { get; set; }
}
