namespace RentNearBy.Core.Entities.Buddy;

// The ledger; "Open" is never stored.
public class TripPayment
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public int Amount { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTime SaidPaidAt { get; set; }
    public Guid SaidPaidByUserId { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public Guid? ReceivedByUserId { get; set; }
    public DateTime? EndedAt { get; set; }
    public Guid? EndedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}
