namespace RentNearBy.Core.DTOs.Owner;

// GET /owner/home (contract 2.6 H1, AU-19, R-2). Codes, args and numbers only; the app owns every word.
public sealed class OwnerHomeDto
{
    public string Role { get; set; } = string.Empty;
    public DateOnly Today { get; set; }
    public bool PaymentEnabled { get; set; }
    public OwnerHomeListingDto Listing { get; set; } = new();
    public List<OwnerAttentionDto> Attention { get; set; } = new();
    public int AttentionCount { get; set; }
    public OwnerHomeNumbersDto Numbers { get; set; } = new();
    public OwnerUpcomingDto Upcoming { get; set; } = new();
    public OwnerPlanCardDto? PlanCard { get; set; }
    public OwnerFestivalDto? Festival { get; set; }
}

public sealed class OwnerHomeListingDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool EverApproved { get; set; }
    public string? Name { get; set; }
    public string? Sub { get; set; }
    public string? CityName { get; set; }
    public string? Plate { get; set; }
    public bool SwitchOn { get; set; }
    public bool IsFeatured { get; set; }
    public bool TakenDown { get; set; }
    public string? RejectedReason { get; set; }
    public bool AreaPaused { get; set; }
    public bool TabOff { get; set; }
    public bool PlannerOff { get; set; }
    public bool PendingEdit { get; set; }
    public bool? RingReady { get; set; }
    public DateOnly? OnTripUntil { get; set; }
    public OwnerMeterDto? RoutesMeter { get; set; }
    public OwnerStatusLineDto? StatusLine { get; set; }
}

public sealed record OwnerMeterDto(int Used, int Limit);

public sealed record OwnerStatusLineDto(string Code, IReadOnlyDictionary<string, object?> Args);

public sealed record OwnerAttentionDto(string Code, int Priority, IReadOnlyDictionary<string, object?> Args);

public sealed class OwnerHomeNumbersDto
{
    public OwnerStayNumbersDto? Stay { get; set; }
    public OwnerTaxiNumbersDto? Taxi { get; set; }
    public int CentreBadge { get; set; }
}

public sealed record OwnerStayNumbersDto(int NewEnquiries, int OffersWaiting, int Confirmed);

public sealed record OwnerTaxiNumbersDto(int NewRequests, DateOnly? NextBookingDate, double? RatingAvg, int RatingCount);

// The first 3 upcoming rows (a subset of the calendar row; no money note field ever leaves here except the chip).
public sealed class OwnerUpcomingRowDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public TimeOnly? TimeOfDay { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? FromPlace { get; set; }
    public string? ToPlace { get; set; }
    public string Chip { get; set; } = string.Empty;
    public int ChipAmount { get; set; }
    public bool Cancelled { get; set; }
    public bool TravellerRemoved { get; set; }
}

public sealed class OwnerUpcomingDto
{
    public List<OwnerUpcomingRowDto> Rows { get; set; } = new();
    public int MoreInWindowCount { get; set; }
    public int WindowLabelDays { get; set; }
    public int BeyondWindowAppCount { get; set; }
    public DateOnly? BeyondWindowFirstDate { get; set; }
    public WindowDto? Window { get; set; }
}

public sealed record OwnerHiddenDto(int Photos, int RoomTypes, int Routes, int SpecialPrices);

public sealed record OwnerWeeklyDto(int? Shown, int? Enquiries, int? RequestsNoRoute, int? RequestsOnRoutes, int? Accepted);

public sealed class OwnerPlanCardDto
{
    public string State { get; set; } = string.Empty;
    public bool PaymentEnabled { get; set; }
    public bool IsPaid { get; set; }
    public string? Source { get; set; }
    public DateTime? EndsAt { get; set; }
    public DateTime? RenewsOn { get; set; }
    public bool TrialAvailable { get; set; }
    public Responses.MyPlanLimitsDto Limits { get; set; } = new();
    public OwnerHiddenDto Hidden { get; set; } = new(0, 0, 0, 0);
    public int? RingWeight { get; set; }
    public DateOnly? PaymentStartsOn { get; set; }
    public bool PlansStartedToday { get; set; }
    public OwnerWeeklyDto Weekly { get; set; } = new(null, null, null, null, null);
}

public sealed record OwnerFestivalDto(Guid Id, string Name, DateOnly DateFrom, DateOnly DateTo, string? StateName, int DaysAway);
