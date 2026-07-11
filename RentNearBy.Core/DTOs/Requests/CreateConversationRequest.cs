namespace RentNearBy.Core.DTOs.Requests;

public class CreateConversationRequest
{
    public string ListingType { get; set; } = string.Empty; // "Room" | "Plot"
    public Guid ListingId { get; set; }
}
