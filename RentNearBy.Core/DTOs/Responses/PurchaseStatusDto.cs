namespace RentNearBy.Core.DTOs.Responses;

public class PurchaseStatusDto
{
    public Guid PurchaseId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public int GrantedDays { get; set; }
    public DateTime? EndsAt { get; set; }
    public string? FailureReason { get; set; }
    public string? GrantBlockedReason { get; set; }
    public string Provider { get; set; } = string.Empty;
}

public class PurchaseHistoryItemDto
{
    public Guid Id { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public int? CycleMonths { get; set; }
    public int PayableInr { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? GrantedAt { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? PlayOrderId { get; set; }
}
