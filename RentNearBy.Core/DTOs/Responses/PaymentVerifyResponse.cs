namespace RentNearBy.Core.DTOs.Responses;

public class PaymentVerifyResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Guid UserMembershipId { get; set; }
    public DateTime ValidUntil { get; set; }
    public string PlanType { get; set; } = string.Empty;
    public int MaxRooms { get; set; }
}
