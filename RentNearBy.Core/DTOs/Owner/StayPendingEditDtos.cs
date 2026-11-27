namespace RentNearBy.Core.DTOs.Owner;

// M1 pendingEdit (AU-5): only the changed review fields, with the names needed to show them.
public sealed class StayPendingEditFieldsDto
{
    public string? Name { get; set; }
    public string? RegistrationTypeName { get; set; }
    public string? RegistrationOtherName { get; set; }
    public bool? RegistrationNumberChanged { get; set; }
    public bool? GstinChanged { get; set; }
    public string? Address { get; set; }
    public string? DistrictName { get; set; }
    public string? CityName { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}

public sealed record StayPendingEditDto(StayPendingEditFieldsDto Fields, IReadOnlyList<string> FieldsChanged, DateTime At);

// The o_st_64 result strip; null after 7 days or while a newer change waits.
public sealed record StayEditResultDto(string Result, string? Reason, DateTime At, IReadOnlyList<string> FieldsChanged);

// o_st_13 calendar hub row and o_st_14 special prices hub row (AU-21).
// FB-45: TravellerRemoved = an app booking whose traveller deleted his account (the label can be empty); the app shows "Guest".
public sealed record StayCalendarRowNextDto(DateOnly Date, string Label, bool TravellerRemoved = false);

public sealed record StayCalendarRowDto(int ThisMonthCount, StayCalendarRowNextDto? Next);

public sealed record StaySpecialPriceFirstDto(string? Label, DateOnly DateFrom, DateOnly DateTo, string Direction, int Percent);

public sealed record StaySpecialPricesRowDto(int Count, int Limit, StaySpecialPriceFirstDto? First);

// Admin review queue (A3, o_pl_19).
public sealed record StayPendingFieldDto(string Field, string? Live, string? Asked);

public sealed record StayPinDto(decimal? Latitude, decimal? Longitude);

public sealed record AdminStayPendingEditRowDto(
    Guid StayId,
    string? StayName,
    string? OwnerName,
    DateTime At,
    IReadOnlyList<StayPendingFieldDto> Fields,
    int? PinMovedMeters,
    StayPinDto LivePin,
    StayPinDto AskedPin,
    string? Note = null,
    bool NameMixedScript = false);

public sealed record AdminStayPendingEditPageDto(IReadOnlyList<AdminStayPendingEditRowDto> Items, string? NextCursor, int? WaitingCount);

public sealed class AdminStayEditDecisionRequest
{
    public DateTime? PendingEditAt { get; set; }
    public string? Reason { get; set; }
}
