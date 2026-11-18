namespace RentNearBy.Core.Entities.TaxiRequests;

// Never auto-deleted. One rating per booking.
public class TaxiRating
{
    public Guid BookingId { get; set; }
    public Guid DriverListingId { get; set; }
    public short Stars { get; set; }
    public DateTime CreatedAt { get; set; }
}
