namespace RentNearBy.Core.DTOs.Responses;

// RequiresPayment false = already granted (Rs 0 order); the app skips checkout.
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
}
