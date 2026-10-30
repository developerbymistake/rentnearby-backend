namespace RentNearBy.Core.DTOs.Responses;

// RequiresPayment false = already granted (Rs 0 order); the app skips checkout. The app routes on Provider, not RequiresPayment.
public class CreatePlanOrderDto
{
    public Guid PurchaseId { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool RequiresPayment { get; set; }
    public string? RazorpayOrderId { get; set; }
    public string? KeyId { get; set; }
    public int AmountPaise { get; set; }
    public string Currency { get; set; } = "INR";
    public DateTime ExpiresAt { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public int GrantedDays { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Intent { get; set; } = string.Empty;
    public string? PlayProductId { get; set; }
    public string? PlayBasePlanId { get; set; }
    public string? PlayOfferId { get; set; }
    public string? PlayOldPurchaseToken { get; set; }
    public string? PlayReplacementMode { get; set; }
    public string? ObfuscatedAccountId { get; set; }
}
