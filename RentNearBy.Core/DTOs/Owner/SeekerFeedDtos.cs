namespace RentNearBy.Core.DTOs.Owner;

// GET /home/seeker-feed (contract 2.6 H2, AU-20): one call for the Seeker Home trip card, status line and plan slots.
public sealed class SeekerFeedDto
{
    public SeekerTripCardDto TripCard { get; set; } = new();
    public SeekerStatusDto? Status { get; set; }
    public SeekerSlotsDto Slots { get; set; } = new(0, 0, false);
}

public sealed class SeekerTripCardDto
{
    public string State { get; set; } = "None";
    public Guid? TripId { get; set; }
    public string? Title { get; set; }
    public string? DestinationName { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }
    public SeekerNextStepDto? NextStep { get; set; }
    public SeekerTodayDto? Today { get; set; }
}

public sealed record SeekerNextStepDto(string Key, string Kind, string? CityName);

public sealed class SeekerTodayDto
{
    public int DayNumber { get; set; }
    public int TotalDays { get; set; }
    public string? CityName { get; set; }
    public int PlaceCount { get; set; }
    public List<SeekerPlaceDto> Places { get; set; } = new();
    // Always null in this round: no visited state exists (AU-20 decision); the app shows the count alone.
    public int? PlacesSeen { get; set; }
}

public sealed record SeekerPlaceDto(string Id, string Name);

public sealed record SeekerStatusDto(string Kind, int Count, string ListingName, Guid? EnquiryId, Guid TripId, string Key);

public sealed record SeekerSlotsDto(int Count, int Limit, bool Full);
