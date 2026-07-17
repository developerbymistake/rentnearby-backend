namespace RentNearBy.Core.Entities;

public class CoinPackPurchase
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid CoinPackId { get; set; }

    // Snapshotted from the CoinPack at purchase time — a later admin price edit must not retroactively
    // change what an already-placed order is worth.
    public int Coins { get; set; }
    public int BonusCoins { get; set; }
    public int PriceInr { get; set; }

    public string Status { get; set; } = string.Empty; // RentNearBy.Core.Models.CoinPackPurchaseStatuses.*
    public string? RazorpayOrderId { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public string? RazorpaySignature { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
