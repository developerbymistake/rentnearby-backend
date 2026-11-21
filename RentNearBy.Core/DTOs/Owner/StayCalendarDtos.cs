using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Core.DTOs.Owner;

// Stay Owner calendar (contract 2.1, 2.2). Owner-only: money notes, manual entries and block reasons never leave these DTOs.
public sealed record StayCalendarEntryDto(
    Guid Id, string Kind, string? SourceType, Guid? SourceId, bool? SourceAvailable, bool TravellerRemoved,
    DateOnly DateFrom, DateOnly DateTo, string? TimeOfDay, string? FromPlace, string? ToPlace,
    string Label, string? Detail, string? Note, int? ListedTotal, int? AgreedTotal,
    short? Guests, short? Seats, string? RoomNames, IReadOnlyList<string> Places, string? CancelledBy, DateOnly? CancelledOn,
    int? FinalAmount, int? AdvanceReceived, bool PaidInFull, int Received, int? Balance, string Chip, int ChipAmount,
    bool Cancelled, int Version)
{
    public static StayCalendarEntryDto From(CalendarEntryRow r)
    {
        var money = CalendarEntryView.Money(r);
        return new(r.Id, r.Kind, r.SourceType, r.SourceId, r.SourceAvailable, CalendarEntryView.TravellerRemoved(r),
            r.DateFrom, r.DateTo, CalendarEntryView.Time(r), r.FromPlace, r.ToPlace, r.Label, r.Detail, r.Note, r.ListedTotal, r.AgreedTotal,
            r.Guests, r.Seats, r.RoomNames, CalendarEntryView.Places(r), r.CancelledBy, r.CancelledOn,
            r.FinalAmount, r.AdvanceReceived, r.PaidInFull, money.Received, money.Balance, money.Chip, money.ChipAmount,
            r.Cancelled, r.Version);
    }

    public static List<StayCalendarEntryDto> From(IEnumerable<CalendarEntryRow> rows) => rows.Select(From).ToList();
}

public sealed record StayBlockedRangeDto(Guid Id, DateOnly DateFrom, DateOnly DateTo, int Nights, string? Reason, int Version);

// The owner's own special prices as drawn on his calendar (read only here; lane BE2 owns the writes).
public sealed record StayCalendarSpecialPriceDto(
    Guid Id, DateOnly DateFrom, DateOnly DateTo, string Direction, int Percent, string? Label, bool IsPaused, bool PausedByPlan);

public sealed record StayCalendarMonthDto(
    IReadOnlyList<StayCalendarEntryDto> Entries,
    IReadOnlyList<StayBlockedRangeDto> Blocks,
    IReadOnlyList<StayCalendarSpecialPriceDto> SpecialPrices,
    IReadOnlyList<CalendarFestivalDto> Festivals,
    IReadOnlyList<MeterDto> Meters,
    WindowDto Window,
    int HiddenCount,
    CalendarReminderDto Reminder,
    bool SummaryAllowed,
    bool PaymentEnabled,
    DateOnly Today);

public sealed record StayCalendarUpcomingDto(
    IReadOnlyList<StayCalendarEntryDto> Items, string? NextCursor, int? HiddenCount, int? BeyondWindowAppCount, DateOnly? BeyondWindowFirstDate);

public sealed record StayCalendarEntryWriteDto(StayCalendarEntryDto Entry, IReadOnlyList<MeterDto> Meters, CalendarOverlapDto Overlap);

public sealed record StayMoneySummaryDto(
    string Month, long FinalTotal, long ReceivedTotal, long BalanceTotal, int EntryCount, int DueCount,
    IReadOnlyList<StayCalendarEntryDto> TopBalances, bool PaymentEnabled);

public sealed record StayBlockedRangeListDto(IReadOnlyList<StayBlockedRangeDto> Items, string? NextCursor, IReadOnlyList<MeterDto> Meters, bool PaymentEnabled);

public sealed record StayBlockedRangeWriteDto(StayBlockedRangeDto Block, int ConfirmedInside, int OpenEnquiriesInside, IReadOnlyList<MeterDto> Meters);

public sealed record StayBlockedRangeConfirmDto(int ConfirmedInside, int OpenEnquiriesInside);

// C5 body (client-made id = idempotent create) and C6 body (version-guarded replace of the editable fields).
public sealed class StayCalendarEntryRequest
{
    public Guid Id { get; set; }
    public int? Version { get; set; }
    public string? Label { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public string? Note { get; set; }
    public int? FinalAmount { get; set; }
    public int? AdvanceReceived { get; set; }
    public bool PaidInFull { get; set; }
}

public sealed class StayMoneyNoteRequest
{
    public int? Version { get; set; }
    public int? FinalAmount { get; set; }
    public int? AdvanceReceived { get; set; }
    public bool PaidInFull { get; set; }
}

public sealed class StayBlockedRangeRequest
{
    public Guid Id { get; set; }
    public int? Version { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public string? Reason { get; set; }
}

public sealed class StayReminderRequest
{
    public bool On { get; set; }
}
