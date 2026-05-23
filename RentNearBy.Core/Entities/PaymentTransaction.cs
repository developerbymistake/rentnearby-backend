namespace RentNearBy.Core.Entities;

public class PaymentTransaction
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public Guid? ListingId { get; set; }
    public Guid? PlotId { get; set; }
    public string? TransactionKind { get; set; } // null = room, "PLOT" = plot
    public string PlanType { get; set; } = string.Empty; // FREE or PAID
    public int Amount { get; set; } // In INR (0 for FREE, 99 for PAID)
    public string Currency { get; set; } = "INR";
    public string? RazorpayOrderId { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public string? RazorpaySignature { get; set; }
    public string Status { get; set; } = string.Empty; // PENDING, SUCCESS, FAILED
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public User? User { get; set; }
    public Listing? Listing { get; set; }
    public Plot? Plot { get; set; }
}
