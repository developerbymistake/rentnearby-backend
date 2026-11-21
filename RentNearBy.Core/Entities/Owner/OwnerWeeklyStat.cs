namespace RentNearBy.Core.Entities.Owner;

// Counts only, no user id (doc 6.10). The other weekly numbers are counted live.
public class OwnerWeeklyStat
{
    public string ListingKind { get; set; } = string.Empty;
    public Guid ListingId { get; set; }
    public DateOnly Day { get; set; }
    public int Shown { get; set; }
    public int RequestsNoRoute { get; set; }
}
