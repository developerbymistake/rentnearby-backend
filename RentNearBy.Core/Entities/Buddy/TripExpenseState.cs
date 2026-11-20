namespace RentNearBy.Core.Entities.Buddy;

// One row per trip; the only counters of the feature. Written only by ITripLedger.RefreshStateAsync.
public class TripExpenseState
{
    public Guid TripId { get; set; }
    public long Version { get; set; }
    public int ExpenseCount { get; set; }
    public long TotalSpent { get; set; }
    public int OpenPayments { get; set; }
    public int SaidPaidCount { get; set; }
    public int ReceivedCount { get; set; }
    public DateTime? ReopenedAt { get; set; }
    public Guid? ReopenedExpenseId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
