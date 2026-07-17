namespace RentNearBy.Core.DTOs.Responses;

public class CoinTransactionWithUserResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserPhoneNumber { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public int Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? ReferenceId { get; set; }
    public int BalanceAfter { get; set; }
    public string? Note { get; set; }
    public Guid? PerformedByUserId { get; set; }
    public string? PerformedByUserPhoneNumber { get; set; }
    public DateTime CreatedAt { get; set; }
}
