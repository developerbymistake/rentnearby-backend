namespace RentNearBy.Core.DTOs.Responses;

public class TripListCountsDto
{
    public int Upcoming { get; set; }
    public int Completed { get; set; }
}

public class TripProblemResultDto
{
    public Guid ProblemId { get; set; }
    // The failed row (new when a multi-night booking was split, else the whole row).
    public TripEnquiryDto? Enquiry { get; set; }
    // The shrunk original that keeps the nights already used; null when the whole row failed.
    public TripEnquiryDto? KeptEnquiry { get; set; }
    public bool Marked { get; set; }
}

public class TripSummaryDayDto
{
    public int Day { get; set; }
    public DateOnly? Date { get; set; }
    public List<string> Spots { get; set; } = new();
    public int Minutes { get; set; }
}

public class TripSummaryRoomLineDto
{
    public string? RoomName { get; set; }
    public int Units { get; set; }
    public int Guests { get; set; }
}

public class TripSummaryNightDto
{
    public int Night { get; set; }
    public DateOnly? Date { get; set; }
    public string ListingName { get; set; } = string.Empty;
    public Guid? EnquiryId { get; set; }
    public bool IsOwn { get; set; }
    public string? OwnPhone { get; set; }
    public string DisplayStatus { get; set; } = string.Empty;
    public List<TripSummaryRoomLineDto> RoomLines { get; set; } = new();
    public int? AgreedTotal { get; set; }
    public string? AdvanceStatus { get; set; }
    public int? AdvanceAmount { get; set; }
    public string? CancellationText { get; set; }
    public bool? GstInvoice { get; set; }
    // "both" | "you" | null
    public string? ConfirmedBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    // "traveller" | "owner" | null
    public string? CancelledBy { get; set; }
}

public class TripSummaryTaxiDto
{
    public Guid? EnquiryId { get; set; }
    public string ListingName { get; set; } = string.Empty;
    public string? VehicleTypeName { get; set; }
    public List<int> Days { get; set; } = new();
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    public bool IsOwn { get; set; }
    public string? OwnPhone { get; set; }
    public string DisplayStatus { get; set; } = string.Empty;
    public int? AgreedFare { get; set; }
    public bool TollExtra { get; set; }
    public string? PickupTime { get; set; }
    public string? PickupPlace { get; set; }
    public string? ConfirmedBy { get; set; }
    public string? CancelledBy { get; set; }
}

public class TripSummaryRecordDto
{
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    // Ordered city names of the trip; one entry on a one-city trip.
    public List<string> LegCities { get; set; } = new();
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? Adults { get; set; }
    public int? Kids { get; set; }
    public string BookingState { get; set; } = string.Empty;
    public int AgreedTotal { get; set; }
    public DateTime GeneratedAt { get; set; }
    public List<TripSummaryDayDto> Days { get; set; } = new();
    public List<TripSummaryNightDto> Nights { get; set; } = new();
    public List<TripSummaryTaxiDto> Taxis { get; set; } = new();
    public RentNearBy.Core.Models.TripSteps? Steps { get; set; }
    public TripJourneyDto? GettingThere { get; set; }
    public TripJourneyDto? ComingBack { get; set; }
    // One entry per city (one on a one-city trip); journey cars live only under Legs[].Travel.Cars.
    public List<TripSummaryLegDto> Legs { get; set; } = new();
}

public class TripSummaryLegDto
{
    public int Index { get; set; }
    public string CityName { get; set; } = string.Empty;
    public int StartDay { get; set; }
    public int EndDay { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public List<TripSummaryNightDto> Nights { get; set; } = new();
    public List<TripSummaryTaxiDto> Taxis { get; set; } = new();
    // Travel into this city; null on leg 1 and when no travel is chosen.
    public TripSummaryTravelDto? Travel { get; set; }
}

public class TripSummaryTravelDto
{
    // "journey" | "own"
    public string Mode { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string ToName { get; set; } = string.Empty;
    public int Day { get; set; }
    public DateOnly? Date { get; set; }
    // TripTravelStates value
    public string State { get; set; } = string.Empty;
    public int CarCount { get; set; }
    public int? ListedTotal { get; set; }
    public List<TripSummaryTravelCarDto> Cars { get; set; } = new();
    public string? How { get; set; }
    public string? OwnName { get; set; }
    public string? OwnPhone { get; set; }
    public string? LeavesAt { get; set; }
    public string? HubName { get; set; }
    public int? RoadMinutes { get; set; }
    public Guid? JourneyId { get; set; }
}

public class TripSummaryTravelCarDto
{
    public int Car { get; set; }
    public string DriverName { get; set; } = string.Empty;
    public string? VehicleTypeName { get; set; }
    public string FromName { get; set; } = string.Empty;
    public string ToName { get; set; } = string.Empty;
    public int? AgreedFare { get; set; }
    public int? ListedFare { get; set; }
    public int? LastQuotedFare { get; set; }
    // TripDisplayStatuses value, or "none" when the car was never asked
    public string DisplayStatus { get; set; } = string.Empty;
    public string? ConfirmedBy { get; set; }
    public Guid? EnquiryId { get; set; }
    public string? PickupTime { get; set; }
    public bool TollExtra { get; set; }
    public string? CancelledBy { get; set; }
    public Guid TaxiId { get; set; }
}
