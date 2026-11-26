namespace RentNearBy.Core.Entities.Owner;

// A day the driver marked not available (IST calendar day). Reason is private to the driver.
public class TaxiDayOff
{
    public Guid TaxiListingId { get; set; }
    public DateOnly Date { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
