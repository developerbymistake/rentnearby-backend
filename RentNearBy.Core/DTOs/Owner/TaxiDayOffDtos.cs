namespace RentNearBy.Core.DTOs.Owner;

// Contract 1.3 A1/A2 and R3. The reason leaves the server only in this driver-only payload.
public sealed record TaxiDayOffDto(DateOnly Date, string? Reason);

// month = IST "yyyy-MM"; the same object in the days-off answers and in numbers.taxi.availability of the Home feed.
public sealed record TaxiAvailabilityCountsDto(string Month, int Bookings, int DaysOff);

public sealed record TaxiDaysOffStateDto(
    DateOnly Today,
    WindowDto Window,
    int MonthsAhead,
    bool SwitchOn,
    IReadOnlyList<TaxiDayOffDto> DaysOff,
    IReadOnlyList<DateOnly> BookedDays,
    TaxiAvailabilityCountsDto Counts);

// Dates are read as strings and parsed by the server (yyyy-MM-dd only). opId is accepted and ignored (R5).
public sealed class TaxiDaysOffSaveRequest
{
    public List<TaxiDayOffSetItem>? Set { get; set; }
    public List<string?>? Clear { get; set; }
    public string? OpId { get; set; }
}

public sealed class TaxiDayOffSetItem
{
    public string? Date { get; set; }
    public string? Reason { get; set; }
}
