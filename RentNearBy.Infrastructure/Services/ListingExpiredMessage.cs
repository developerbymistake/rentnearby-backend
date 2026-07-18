namespace RentNearBy.Infrastructure.Services;

public class ListingExpiredMessage
{
    public Guid UserId { get; set; }
    public Guid ListingId { get; set; }
    public string ListingKind { get; set; } = string.Empty; // RentNearBy.Core.Models.ListingKinds.*
    public DateTime ExpiredAt { get; set; }
}
