namespace RentNearBy.Core.Entities;

public class CreditTransaction
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int Amount { get; set; } // positive = credit, negative = debit
    public string Reason { get; set; } = string.Empty; // RentNearBy.Core.Models.CreditTransactionReasons.*
    public Guid? ReferenceId { get; set; } // meaning depends on Reason (CreditPackPurchaseId, CouponRedemption.Id, RoomListingId, PlotId, admin-supplied idempotency key...)
    public int BalanceAfter { get; set; }
    public Guid? PerformedByUserId { get; set; } // set only for admin-initiated credits/debits
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public User? PerformedByUser { get; set; }
}
