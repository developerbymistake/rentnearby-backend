namespace RentNearBy.Core.Entities.Buddy;

// Append only.
public class TripExpenseRevision
{
    public long Id { get; set; }
    public Guid TripId { get; set; }
    public Guid ExpenseId { get; set; }
    public int Version { get; set; }
    public Guid OpId { get; set; }
    public Guid ActorUserId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ChangesJson { get; set; } = "[]";
    public string SharesAfterJson { get; set; } = "[]";
    public int AmountAfter { get; set; }
    public DateTime CreatedAt { get; set; }
}
