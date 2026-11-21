namespace RentNearBy.Core.Entities.Owner;

// One morning reminder run per IST day and listing kind; LastListingId is the resume cursor.
public class OwnerReminderRun
{
    public DateOnly RunDate { get; set; }
    public string ListingKind { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public Guid? LastListingId { get; set; }
    public int Sent { get; set; }
}
