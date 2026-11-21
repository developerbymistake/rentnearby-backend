namespace RentNearBy.Core.Entities.Owner;

// Inclusive nights the owner keeps closed. Reason is owner-only.
public class StayBlockedRange
{
    public Guid Id { get; set; }
    public Guid StayListingId { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string? Reason { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
