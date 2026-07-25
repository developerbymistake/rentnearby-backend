namespace RentNearBy.Core.Entities;

public class CreditPackPurchase
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid CreditPackId { get; set; }

    // Snapshotted from the CreditPack at purchase time — a later admin price edit must not retroactively
    // change what an already-placed order is worth.
    public int Credits { get; set; }
    public int BonusCredits { get; set; }
    public int PriceInr { get; set; }

    public string Status { get; set; } = string.Empty; // RentNearBy.Core.Models.CreditPackPurchaseStatuses.*
    public string? RazorpayOrderId { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public string? RazorpaySignature { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
