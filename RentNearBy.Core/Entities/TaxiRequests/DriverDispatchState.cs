namespace RentNearBy.Core.Entities.TaxiRequests;

// Fair queue position of one driver in one city. Missed rings are counted on TaxiListings.RingMissStreak.
public class DriverDispatchState
{
    public Guid DriverListingId { get; set; }
    public long CityKey { get; set; }
    public long Pass { get; set; }
    public long SeenSeq { get; set; }
    public DateTime? LastOfferedAt { get; set; }
}
