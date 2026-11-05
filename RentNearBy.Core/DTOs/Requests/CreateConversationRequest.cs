namespace RentNearBy.Core.DTOs.Requests;

public class CreateConversationRequest
{
    public string ListingType { get; set; } = string.Empty; // "Stay"
    public Guid ListingId { get; set; }
}
