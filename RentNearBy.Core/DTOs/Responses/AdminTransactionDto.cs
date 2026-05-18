namespace RentNearBy.Core.DTOs.Responses;

public class AdminTransactionDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? UserName { get; set; }
    public string UserPhone { get; set; } = string.Empty;
    public string PlanType { get; set; } = string.Empty;
    public int Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public string Status { get; set; } = string.Empty;
    public string? RazorpayPaymentId { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
