namespace RentNearBy.Infrastructure.Services;

public class GoLiveRequestedMessage
{
    public Guid ListingId { get; set; }
    public string ListingType { get; set; } = string.Empty; // "Room" | "Plot"
}
