namespace RentNearBy.Core.DTOs.Responses;

public class AdminListingLimitDto
{
    public Guid Id { get; set; }
    public string ListingKind { get; set; } = string.Empty;
    public int MaxListings { get; set; }
    public DateTime UpdatedAt { get; set; }
}
