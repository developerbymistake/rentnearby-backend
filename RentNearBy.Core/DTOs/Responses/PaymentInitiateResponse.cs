namespace RentNearBy.Core.DTOs.Responses;

public class PaymentInitiateResponse
{
    public Guid TransactionId { get; set; }
    public string? RazorpayOrderId { get; set; }
    public int Amount { get; set; }
    public string PlanType { get; set; } = string.Empty;
    public string Currency { get; set; } = "INR";
}
