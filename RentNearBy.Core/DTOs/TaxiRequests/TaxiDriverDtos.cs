namespace RentNearBy.Core.DTOs.TaxiRequests;

// Contract section 6 (driver side) and the device state of section 6 readiness.

public class DriverTravellerDto
{
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
}

public class DriverDatesConflictDto
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
}

public class DriverTripPickupDto
{
    public string Name { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
}

public class DriverTripDropDto
{
    public string Name { get; set; } = string.Empty;
    public bool SameAsPickup { get; set; }
    public DateOnly Date { get; set; }
}

public class DriverTripCityDto
{
    public string Name { get; set; } = string.Empty;
    public int Nights { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
}

public class DriverTripDayDto
{
    public int Day { get; set; }
    public DateOnly Date { get; set; }
    public string City { get; set; } = string.Empty;
    public bool Taxi { get; set; }
    public int Km { get; set; }
    public int Minutes { get; set; }
    public List<string> Places { get; set; } = new();
}

public class DriverTripDto
{
    public string Name { get; set; } = string.Empty;
    public DriverTripPickupDto? Pickup { get; set; }
    public DriverTripDropDto? Drop { get; set; }
    public List<DriverTripCityDto> Cities { get; set; } = new();
    public List<DriverTripDayDto> Days { get; set; } = new();
}

// One offer: list items carry trip = null, detail carries the full trip.
public class DriverOfferDto
{
    public Guid OfferId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? BookingId { get; set; }
    public string State { get; set; } = string.Empty;
    public string? EndReason { get; set; }
    public string Bucket { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? ConfirmBy { get; set; }
    public string PickupName { get; set; } = string.Empty;
    public TaxiSectionsDto Sections { get; set; } = new();
    public int SightseeingDayCount { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public int DayCount { get; set; }
    public int NightCount { get; set; }
    public string RouteLine { get; set; } = string.Empty;
    public int Seats { get; set; }
    public int Travellers { get; set; }
    public DriverTravellerDto? Traveller { get; set; }
    public DriverDatesConflictDto? DatesConflict { get; set; }
    public DriverTripDto? Trip { get; set; }
}

public class DriverOfferCountsDto
{
    public int New { get; set; }
    public int Booked { get; set; }
}

public class DriverOfferListDto
{
    public DateTime ServerNow { get; set; }
    public DriverOfferCountsDto Counts { get; set; } = new();
    public string? NextCursor { get; set; }
    public List<DriverOfferDto> Items { get; set; } = new();
}

public class DeclineOfferDto
{
    public string? Chip { get; set; }
}

public class WithdrawOfferDto
{
    public string? Chip { get; set; }
}

public class DriverOccupiedDto
{
    public Guid BookingId { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
}

public class DriverBookingDto
{
    public Guid BookingId { get; set; }
    public Guid OfferId { get; set; }
    public Guid RequestId { get; set; }
    public string State { get; set; } = string.Empty;
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string PickupName { get; set; } = string.Empty;
    public string? TravellerName { get; set; }
    public string? TravellerPhone { get; set; }
    public bool CanWithdraw { get; set; }
    public bool CanEnd { get; set; }
}

public class DriverBookingsDto
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public List<DriverOccupiedDto> Occupied { get; set; } = new();
    public List<DriverBookingDto> Items { get; set; } = new();
}

public class DriverRatingRowDto
{
    public DateOnly Date { get; set; }
    public int Stars { get; set; }
}

public class DriverRatingDto
{
    public double? Average { get; set; }
    public int RatingCount { get; set; }
    public int RidesCompleted { get; set; }
    public List<DriverRatingRowDto> Latest { get; set; } = new();
}

public class DriverReadinessDto
{
    public bool Approved { get; set; }
    public bool Online { get; set; }
    public bool RingReady { get; set; }
    public bool NotificationsOn { get; set; }
    public bool HasValidToken { get; set; }
    public bool MissedRings { get; set; }
    public int RoutesWithoutCity { get; set; }
}

public class DriverDeviceStateDto
{
    public int AppBuild { get; set; }
    public bool NotificationsOn { get; set; }
    public bool RingChannelOn { get; set; }
    public bool BatteryUnrestricted { get; set; }
    public string? Manufacturer { get; set; }
    public int AndroidSdk { get; set; }
    public bool RingTested { get; set; }
}
