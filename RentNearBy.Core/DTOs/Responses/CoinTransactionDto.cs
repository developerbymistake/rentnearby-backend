namespace RentNearBy.Core.DTOs.Responses;

public class CoinTransactionDto
{
    public Guid Id { get; set; }
    public int Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? ReferenceId { get; set; }
    public int BalanceAfter { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}
