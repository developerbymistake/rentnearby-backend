namespace RentNearBy.Core.DTOs.TaxiRequests;

// Contract sections 5 and 9. Enums are strings, dates DateOnly, instants UTC DateTime, ids Guid; JSON is the API default (camelCase, nulls written).

public class TaxiCityRefDto
{
    public long CityKey { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class TaxiPickupPlaceDto
{
    public long CityKey { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Drivers { get; set; }
}

public class PickupPlacesDto
{
    public bool Paused { get; set; }
    public TaxiCityRefDto? FirstCity { get; set; }
    public TaxiCityRefDto? LastCity { get; set; }
    public List<TaxiPickupPlaceDto> Pickup { get; set; } = new();
    public List<TaxiPickupPlaceDto> Drop { get; set; } = new();
    public int SightseeingDrivers { get; set; }
    public int Travellers { get; set; }
    public int SuggestedSeats { get; set; }
    // O4: drivers per trip city for sightseeing (the city of the first chosen sightseeing day decides). Additive field.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<TaxiSightseeingCityDto>? SightseeingCities { get; set; }
    // O2: seat choices of a request and the largest group one request takes.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? SeatOptions { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTravellers { get; set; }
}

public class TaxiSightseeingCityDto
{
    public long CityKey { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<int> Days { get; set; } = new();
    public int Drivers { get; set; }
}

public class TaxiSectionsDto
{
    public bool Pickup { get; set; }
    public bool Sightseeing { get; set; }
    public bool Drop { get; set; }
}

public class CreateTaxiPickupDto
{
    public bool On { get; set; }
    public long? CityKey { get; set; }
}

public class CreateTaxiSightseeingDto
{
    public bool On { get; set; }
    public List<int> Days { get; set; } = new();
}

public class CreateTaxiDropDto
{
    public bool On { get; set; }
    public bool SameAsPickup { get; set; }
    public long? CityKey { get; set; }
}

public class TaxiDayStatDto
{
    public int Day { get; set; }
    public int Km { get; set; }
    public int Minutes { get; set; }
}

public class CreateTaxiRequestDto
{
    public Guid? ClientRequestId { get; set; }
    public CreateTaxiPickupDto Pickup { get; set; } = new();
    public CreateTaxiSightseeingDto Sightseeing { get; set; } = new();
    public CreateTaxiDropDto Drop { get; set; } = new();
    public int Seats { get; set; }
    public List<TaxiDayStatDto> DayStats { get; set; } = new();
}

public class CancelTaxiRequestDto
{
    public string Reason { get; set; } = string.Empty;
    public string? Chip { get; set; }
}

public class RateTaxiRequestDto
{
    public int Stars { get; set; }
}

public class TaxiRequestDropDto
{
    public long CityKey { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool SameAsPickup { get; set; }
}

public class TaxiRequestDriverDto
{
    public Guid OfferId { get; set; }
    public string State { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string Vehicle { get; set; } = string.Empty;
    public int Seats { get; set; }
    public double? RatingAverage { get; set; }
    public int RatingCount { get; set; }
    public int RidesCompleted { get; set; }
    public bool CanSelect { get; set; }
    public DateTime? ConfirmBy { get; set; }
}

public class TaxiRequestBookingDto
{
    public Guid Id { get; set; }
    public string State { get; set; } = string.Empty;
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public int? DayNumber { get; set; }
    public bool CanCancel { get; set; }
    public bool CanEnd { get; set; }
    // O13, added after contract v1: always set by the server; absent only in the v1 samples.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanRate { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? LeftDuringTrip { get; set; }
}

public class TaxiRequestRatingDto
{
    public int Stars { get; set; }
    public DateTime RatedAt { get; set; }
}

public class TaxiRequestDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string TripName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string View { get; set; } = string.Empty;
    public long Version { get; set; }
    public string? EndReason { get; set; }
    public TaxiSectionsDto Sections { get; set; } = new();
    public TaxiCityRefDto? Pickup { get; set; }
    public TaxiRequestDropDto? Drop { get; set; }
    public int Seats { get; set; }
    public int Travellers { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public int DayCount { get; set; }
    public List<int> SightseeingDays { get; set; } = new();
    public int AskedCount { get; set; }
    public int RingingCount { get; set; }
    public int AcceptedCount { get; set; }
    public bool StillAsking { get; set; }
    public bool CanAskMore { get; set; }
    public bool CanCancel { get; set; }
    public DateTime? WaitUntil { get; set; }
    public List<TaxiRequestDriverDto> Drivers { get; set; } = new();
    public TaxiRequestBookingDto? Booking { get; set; }
    public TaxiRequestRatingDto? Rating { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// data of 409 LegacyTaxiActive.
public class LegacyTaxiEnquiryDto
{
    public Guid Id { get; set; }
    public bool Booked { get; set; }
}

public class LegacyTaxiActiveData
{
    public List<LegacyTaxiEnquiryDto> Enquiries { get; set; } = new();
}

// data of 409 PlanLocked.
public class PlanLockedData
{
    public string Reason { get; set; } = string.Empty;
    public Guid? RequestId { get; set; }
}

// data of 409 TaxiRequestAlreadyActive.
public class TaxiRequestAlreadyActiveData
{
    public Guid RequestId { get; set; }
}

// data of 409 OverlappingTaxiRequest: the other trip that already holds a taxi on these dates.
public class OverlappingTaxiRequestData
{
    public Guid TripId { get; set; }
    public string TripName { get; set; } = string.Empty;
    public Guid RequestId { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
}

// data of 409 TaxiRequestStateInvalid.
public class TaxiRequestStateInvalidData
{
    public string State { get; set; } = string.Empty;
    public long Version { get; set; }
}

// data of 409 DatesAlreadyBooked.
public class DatesAlreadyBookedData
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
}

// data of 409 OfferMissed and OfferStateInvalid.
public class OfferStateData
{
    public string State { get; set; } = string.Empty;
}

// SignalR event TaxiRequestChanged (section 9); v is the payload version, unknown values are ignored by clients.
public class TaxiRequestChangedEvent
{
    public int V { get; set; } = 1;
    public Guid RequestId { get; set; }
    public Guid TripId { get; set; }
    public long Version { get; set; }
    public string State { get; set; } = string.Empty;
}
