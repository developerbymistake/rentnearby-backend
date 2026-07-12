namespace RentNearBy.Infrastructure.Services;

public class ReportFiledMessage
{
    public Guid OwnerId { get; set; }
    public Guid ListingId { get; set; }
    public string ListingType { get; set; } = string.Empty; // "Room" | "Plot"
    public string ReasonName { get; set; } = string.Empty;
    public string ListingTitle { get; set; } = string.Empty;
    // False when the listing already had a Pending report — the owner was already
    // notified once, so this event only needs to reach admins this time.
    public bool NotifyOwner { get; set; } = true;
}
