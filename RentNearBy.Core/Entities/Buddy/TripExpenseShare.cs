namespace RentNearBy.Core.Entities.Buddy;

// Live shares only: a soft-deleted expense loses its rows in the same transaction.
public class TripExpenseShare
{
    public Guid ExpenseId { get; set; }
    public Guid UserId { get; set; }
    public Guid TripId { get; set; }
    public int Share { get; set; }
}
