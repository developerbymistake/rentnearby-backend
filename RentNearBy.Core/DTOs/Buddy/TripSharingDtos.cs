namespace RentNearBy.Core.DTOs.Buddy;

// Lane BE2 (contract 2.2). Every field here is on the allow list; nothing else is ever serialised for a viewer.

public sealed record SharingResult<T>(T? Value, string? ErrorType = null, object? ErrorData = null, int? Status = null)
{
    public bool Ok => ErrorType is null;

    public static SharingResult<T> Success(T value) => new(value);

    public static SharingResult<T> Fail(string type, object? data = null, int? status = null) => new(default, type, data, status);
}

public class TripInviteRequest
{
    public List<Guid> UserIds { get; set; } = [];
    public Guid ClientRequestId { get; set; }
}

public class TripMembersDto
{
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public int TravellerCount { get; set; }
    public int MaxTravellers { get; set; }
    public int PendingInvites { get; set; }
    public int SeatsLeft { get; set; }
    public List<TripJoinedPersonDto> Joined { get; set; } = [];
    public List<TripInvitedPersonDto> Invited { get; set; } = [];
    public bool CanInvite { get; set; }
    public bool CanLeave { get; set; }
}

public class TripJoinedPersonDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string? Username { get; set; }
    public string Initial { get; set; } = string.Empty;
    public string Last4 { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsMe { get; set; }
    public DateTime? JoinedAt { get; set; }
}

public class TripInvitedPersonDto
{
    public Guid MemberId { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string? Username { get; set; }
    public string Initial { get; set; } = string.Empty;
    public DateTime InvitedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class TripInviteResultDto
{
    public List<TripInviteOutcomeDto> Results { get; set; } = [];
    public int SeatsLeft { get; set; }
}

public class TripInviteOutcomeDto
{
    public Guid UserId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public Guid? MemberId { get; set; }
}

public class TripStatusDto
{
    public string Status { get; set; } = string.Empty;
}

public class TripJoinedDto
{
    public Guid TripId { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class TripRemovedDto
{
    public string Status { get; set; } = string.Empty;
    public int SharesExpenses { get; set; }
}

public class TripLeftDto
{
    public string Status { get; set; } = string.Empty;
    public bool HasShare { get; set; }
}

public class TripCountDto
{
    public int Count { get; set; }
}

public class TripOwnerRefDto
{
    public string Name { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string? Username { get; set; }
}

public class TripInvitePreviewDto
{
    public Guid MemberId { get; set; }
    public Guid? TripId { get; set; }
    public TripOwnerRefDto Owner { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public List<string> LegCities { get; set; } = [];
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int DayCount { get; set; }
    public int CityCount { get; set; }
    public int TravellerCount { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool AlreadyMember { get; set; }
}

// data of 404 InviteEnded. Reason: Cancelled | Expired | Declined | Unknown.
public class InviteEndedDataDto
{
    public string Reason { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? OwnerName { get; set; }
}

// data of 403 NotOnTrip (S14, former traveller).
public class NotOnTripDataDto
{
    public bool HasShare { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? EndedHow { get; set; }
}

public class TripViewerDto
{
    // Social contract 2.5: joined by request before the start date; stay, taxi and money blocks are empty.
    public bool DetailsLocked { get; set; }

    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public TripOwnerRefDto Owner { get; set; } = new();
    public List<string> LegCities { get; set; } = [];
    public int DayCount { get; set; }
    public int SpotCount { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int TravellerCount { get; set; }
    public List<TravellerChipDto> FirstTravellers { get; set; } = [];
    public List<TripViewerLegDto> Legs { get; set; } = [];
    public List<TripViewerDayDto> Days { get; set; } = [];
    public TripViewerStayDto Stay { get; set; } = new();
    public TripViewerTaxiDto Taxi { get; set; } = new();
    public int ProgressPercent { get; set; }
    public TripExpenseCardDto Expenses { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
    // Trip compaction: always present; an archived trip sends Legs and Days empty and the kept summary (null otherwise).
    public bool Archived { get; set; }
    public RentNearBy.Core.Models.TripArchiveSummary? Summary { get; set; }
}

public class TripViewerLegDto
{
    public int Index { get; set; }
    public string CityName { get; set; } = string.Empty;
    public long CityKey { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int FirstDay { get; set; }
    public int LastDay { get; set; }
    public int Nights { get; set; }
    public int SpotCount { get; set; }
}

public class TripViewerDayDto
{
    public int Day { get; set; }
    public int Leg { get; set; }
    public bool RestDay { get; set; }
    public DateOnly? Date { get; set; }
    public int TotalMinutes { get; set; }
    public int PlaceCount { get; set; }
    public object? RouteGeoJson { get; set; }
    public List<TripViewerSpotDto> Spots { get; set; } = [];
}

public class TripViewerSpotDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int VisitMinutes { get; set; }
    public string? StartTime { get; set; }
    public int? TravelMinutesToNext { get; set; }
}

// State: NotAdded | Arranging | Booked.
public class TripViewerStayDto
{
    public string State { get; set; } = "NotAdded";
    public List<TripViewerStayItemDto> Items { get; set; } = [];
}

public class TripViewerStayItemDto
{
    public int Leg { get; set; }
    public string CityName { get; set; } = string.Empty;
    public string StayName { get; set; } = string.Empty;
    public int Nights { get; set; }
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
}

// State: NotAdded | Arranging | Booked | Completed.
public class TripViewerTaxiDto
{
    public string State { get; set; } = "NotAdded";
    public string? VehicleType { get; set; }
    public string? DriverFirstName { get; set; }
    public DateOnly? PickupDate { get; set; }
    public string? PickupTime { get; set; }
}

public class TripSharedListDto
{
    public List<TripSharedItemDto> Items { get; set; } = [];
    public List<TripInviteCardDto> Invites { get; set; } = [];
    public List<TripFormerCardDto> Former { get; set; } = [];
    public string? NextCursor { get; set; }
    public TripSharedCountsDto Counts { get; set; } = new();
}

public class TripSharedItemDto
{
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Role { get; set; } = "Viewer";
    public string OwnerName { get; set; } = string.Empty;
    public List<string> LegCities { get; set; } = [];
    public int DayCount { get; set; }
    public int SpotCount { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int TravellerCount { get; set; }
    public int ProgressPercent { get; set; }
    public string StayState { get; set; } = string.Empty;
    public string TaxiState { get; set; } = string.Empty;
    public string PlanState { get; set; } = "Saved";
    public DateTime? JoinedAt { get; set; }
    public TripSharedMoneyDto Expenses { get; set; } = new();
    // Trip compaction: always present; LegCities then come from the kept summary.
    public bool Archived { get; set; }
}

public class TripSharedMoneyDto
{
    public bool HasExpenses { get; set; }
    public long Spent { get; set; }
    public int MyShare { get; set; }
    public int OpenPayments { get; set; }
    public bool Settled { get; set; } = true;
}

public class TripInviteCardDto
{
    public Guid MemberId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int DayCount { get; set; }
    public int CityCount { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class TripFormerCardDto
{
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string EndedHow { get; set; } = string.Empty;
    public int MyNet { get; set; }
    public string? OwesToName { get; set; }
    public int SharedCount { get; set; }
}

public class TripSharedCountsDto
{
    public int Upcoming { get; set; }
    public int Past { get; set; }
}

// Option: Available | OnTrip | InviteSent | Full | NoPlaces.
public class TripInviteOptionDto
{
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int DayCount { get; set; }
    public int TravellerCount { get; set; }
    public string Option { get; set; } = string.Empty;
}
