namespace RentNearBy.Core.DTOs.Responses;

public class JourneyDriverDto
{
    public Guid TaxiId { get; set; }
    public string DriverName { get; set; } = string.Empty;
    public string? VehicleTypeName { get; set; }
    public short? Seats { get; set; }
    public string? PlateDisplay { get; set; }
    public string? CityName { get; set; }
    public string TripReplyLabel { get; set; } = "new";
    public string? Phone { get; set; }
    public string? WhatsApp { get; set; }
    public bool Available { get; set; }
}

public class JourneyLegDto
{
    public int LegIndex { get; set; }
    public string LegKey { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public double FromLat { get; set; }
    public double FromLng { get; set; }
    public string ToName { get; set; } = string.Empty;
    public double ToLat { get; set; }
    public double ToLng { get; set; }
    public string? MeetingPlace { get; set; }
    public JourneyDriverDto Driver { get; set; } = new();
    public List<JourneyDriverDto> Backups { get; set; } = new();
    // The whole stretch for the group, priced on the server: the seat cover's fare (driver + CoverDrivers, or driver x CarsNeeded).
    public int? ListedFare { get; set; }
    public int CarsNeeded { get; set; } = 1;
    // The other cars of the seat cover (driver excluded); empty = the driver carries the group alone.
    public List<Guid> CoverTaxiIds { get; set; } = new();
    public List<JourneyDriverDto> CoverDrivers { get; set; } = new();
    public string? SeatsNote { get; set; }
    public bool Partial { get; set; }
    public Guid? LastEnquiryId { get; set; }
    public string LastKnownStatus { get; set; } = "Unknown";
    public int? LastQuotedFare { get; set; }
    public DateTime? LastKnownAt { get; set; }
    public bool DriverAvailable { get; set; }
    public bool FareChanged { get; set; }
    // Set only when this car is booked in a trip: ConfirmedByTraveller | ConfirmedByBoth | BookedByTraveller.
    public string? BookingStatus { get; set; }
    public int? AgreedFare { get; set; }
}

public class JourneyDto
{
    public Guid Id { get; set; }
    // The trip whose plan uses this journey (a step or the travel between two cities).
    public Guid? TripId { get; set; }
    public string? TripTitle { get; set; }
    // gettingThere | comingBack | segment | null
    public string? TripRole { get; set; }
    public int? TripLegIndex { get; set; }
    public int? TripTravelDay { get; set; }
    public string FromName { get; set; } = string.Empty;
    public double FromLat { get; set; }
    public double FromLng { get; set; }
    public string ToName { get; set; } = string.Empty;
    public double ToLat { get; set; }
    public double ToLng { get; set; }
    public DateOnly Date { get; set; }
    public int People { get; set; }
    public string PlanKey { get; set; } = string.Empty;
    public int Cars { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? ListedTotal { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastRefreshedAt { get; set; }
    public List<JourneyLegDto> Legs { get; set; } = new();
}

public class JourneyListDto
{
    public List<JourneyDto> Items { get; set; } = new();
}

public class AskItemDto
{
    public int LegIndex { get; set; }
    public Guid TaxiId { get; set; }
    public Guid EnquiryId { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class AskSkippedDto
{
    public int LegIndex { get; set; }
    public Guid TaxiId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class AskDriversResultDto
{
    public List<AskItemDto> Items { get; set; } = new();
    public List<AskSkippedDto> Skipped { get; set; } = new();
    public bool VeryShortNotice { get; set; }
}
