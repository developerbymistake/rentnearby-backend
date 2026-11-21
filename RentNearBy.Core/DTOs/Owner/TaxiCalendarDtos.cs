using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Core.DTOs.Owner;

// Taxi Driver calendar (contract 2.1). Owner-only: money notes and manual trips never leave these DTOs.
public sealed record TaxiCalendarEntryDto(
    Guid Id, string Kind, string? SourceType, Guid? SourceId, bool? SourceAvailable, bool TravellerRemoved,
    DateOnly DateFrom, DateOnly DateTo, string? TimeOfDay, string? FromPlace, string? ToPlace,
    string Label, string? Detail, string? Note, int? ListedTotal, int? AgreedTotal,
    short? Guests, short? Seats, string? RoomNames, IReadOnlyList<string> Places, string? CancelledBy, DateOnly? CancelledOn,
    int? FinalAmount, int? AdvanceReceived, bool PaidInFull, int Received, int? Balance, string Chip, int ChipAmount,
    bool Cancelled, int Version)
{
    public static TaxiCalendarEntryDto From(CalendarEntryRow r)
    {
        var money = CalendarEntryView.Money(r);
        return new(r.Id, r.Kind, r.SourceType, r.SourceId, r.SourceAvailable, CalendarEntryView.TravellerRemoved(r),
            r.DateFrom, r.DateTo, CalendarEntryView.Time(r), r.FromPlace, r.ToPlace, r.Label, r.Detail, r.Note, r.ListedTotal, r.AgreedTotal,
            r.Guests, r.Seats, r.RoomNames, CalendarEntryView.Places(r), r.CancelledBy, r.CancelledOn,
            r.FinalAmount, r.AdvanceReceived, r.PaidInFull, money.Received, money.Balance, money.Chip, money.ChipAmount,
            r.Cancelled, r.Version);
    }

    public static List<TaxiCalendarEntryDto> From(IEnumerable<CalendarEntryRow> rows) => rows.Select(From).ToList();
}

public sealed record TaxiCalendarMonthDto(
    IReadOnlyList<TaxiCalendarEntryDto> Entries,
    IReadOnlyList<CalendarFestivalDto> Festivals,
    IReadOnlyList<MeterDto> Meters,
    WindowDto Window,
    int HiddenCount,
    CalendarReminderDto Reminder,
    bool SummaryAllowed,
    bool PaymentEnabled,
    DateOnly Today);

public sealed record TaxiCalendarUpcomingDto(
    IReadOnlyList<TaxiCalendarEntryDto> Items, string? NextCursor, int? HiddenCount, int? BeyondWindowAppCount, DateOnly? BeyondWindowFirstDate);

public sealed record TaxiCalendarEntryWriteDto(TaxiCalendarEntryDto Entry, IReadOnlyList<MeterDto> Meters, CalendarOverlapDto Overlap);

public sealed record TaxiMoneySummaryDto(
    string Month, long FinalTotal, long ReceivedTotal, long BalanceTotal, int EntryCount, int DueCount,
    IReadOnlyList<TaxiCalendarEntryDto> TopBalances, bool PaymentEnabled);

// C5 body (client-made id = idempotent create) and C6 body (version-guarded replace). A taxi trip is one day with an optional time.
public sealed class TaxiCalendarEntryRequest
{
    public Guid Id { get; set; }
    public int? Version { get; set; }
    public string? Label { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public string? TimeOfDay { get; set; }
    public string? FromPlace { get; set; }
    public string? ToPlace { get; set; }
    public string? Note { get; set; }
    public int? FinalAmount { get; set; }
    public int? AdvanceReceived { get; set; }
    public bool PaidInFull { get; set; }
}

public sealed class TaxiMoneyNoteRequest
{
    public int? Version { get; set; }
    public int? FinalAmount { get; set; }
    public int? AdvanceReceived { get; set; }
    public bool PaidInFull { get; set; }
}

public sealed class TaxiReminderRequest
{
    public bool On { get; set; }
}
