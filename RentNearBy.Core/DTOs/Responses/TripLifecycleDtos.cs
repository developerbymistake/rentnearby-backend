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
}
