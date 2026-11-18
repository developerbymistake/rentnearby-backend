using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Core.Entities.TaxiRequests;

// Never auto-deleted. No foreign keys, so it outlives the request, trip, listing and users; names are snapshots.
public class TaxiBooking
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid OfferId { get; set; }
    public Guid TripId { get; set; }
    public Guid DriverListingId { get; set; }
    public Guid DriverUserId { get; set; }
    public Guid TravellerUserId { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string Status { get; set; } = BookingStates.Booked;
    public string? CancelledBy { get; set; }
    public string? CancelChip { get; set; }
    public string? CompletedBy { get; set; }

    public string TripName { get; set; } = string.Empty;
    public string PickupCityName { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string VehicleName { get; set; } = string.Empty;
    public short Seats { get; set; }
    public string TravellerName { get; set; } = string.Empty;

    public DateTime BookedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
}
