using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be3.Be3Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be3;

// E1 owner block: booked stays (suggestions + link targets) and taxi bookings (link targets). Two statements per call.
// No plan estimate and no taxi price (decision D2 option 1): a suggestion is a booked stay with its agreed amount only.
public sealed class TripExpenseSuggestions(ApplicationDbContext db)
{
    private static readonly string[] StayStatuses =
    [
        .. TripEnquiryStatuses.Booked, TripEnquiryStatuses.Closed,
        TripEnquiryStatuses.CancelledByTraveller, TripEnquiryStatuses.CancelledByOwner, TripEnquiryStatuses.Failed,
    ];

    private const string StaysSql = """
        SELECT q."Id", q."ListingName", q."Day"::int AS "Day", q."AgreedTotal", q."Status",
               EXISTS (SELECT 1 FROM "TripExpenseSuggestionSkips" k WHERE k."TripId" = @trip AND k."SourceKind" = 'Stay' AND k."SourceId" = q."Id") AS "Skipped",
               EXISTS (SELECT 1 FROM "TripExpenses" e WHERE e."TripId" = @trip AND e."LinkKind" = 'Stay' AND e."LinkId" = q."Id" AND e."DeletedAt" IS NULL) AS "Linked"
        FROM "TripEnquiries" q
        WHERE q."TripId" = @trip AND q."Kind" = 'Stay' AND q."Status" = ANY(@statuses)
        ORDER BY q."Day" NULLS LAST, q."Id"
        """;

    private const string TaxisSql = """
        SELECT b."Id", b."DriverName", b."VehicleName", b."DateFrom", b."Status"
        FROM "TaxiBookings" b WHERE b."TripId" = @trip
        ORDER BY b."DateFrom", b."Id"
        """;

    public sealed record Block(List<ExpenseSuggestionDto> Suggestions, List<ExpenseLinkOptionDto> Links);

    public async Task<Block> LoadAsync(Guid tripId, DateOnly? startDate, int dayCount, bool frozen, CancellationToken ct = default)
    {
        var stays = await db.Database.SqlQueryRaw<StayRow>(StaysSql, Uuid("trip", tripId), Texts("statuses", StayStatuses)).ToListAsync(ct);
        var taxis = await db.Database.SqlQueryRaw<TaxiRow>(TaxisSql, Uuid("trip", tripId)).ToListAsync(ct);
        return Build(stays, taxis, startDate, dayCount, frozen);
    }

    internal static Block Build(IReadOnlyList<StayRow> stays, IReadOnlyList<TaxiRow> taxis, DateOnly? startDate, int dayCount, bool frozen)
    {
        var maxDay = TripExpenseRules.MaxDay(dayCount);
        var suggestions = frozen ? [] : stays
            .Where(s => TripEnquiryStatuses.IsBooked(s.Status) && s.AgreedTotal is > 0 && !s.Skipped && !s.Linked)
            .Select(s => new ExpenseSuggestionDto
            {
                Kind = ExpenseLinkKinds.Stay, SourceId = s.Id, Name = s.ListingName,
                Amount = Math.Min(s.AgreedTotal!.Value, BuddyLimits.MaxAmount), Day = Math.Clamp(s.Day ?? 1, 1, maxDay),
            })
            .ToList();
        var links = stays
            .Where(s => !StayCancelled(s.Status) || s.Linked)
            .Select(s => new ExpenseLinkOptionDto
            {
                Kind = ExpenseLinkKinds.Stay, Id = s.Id, Label = s.ListingName, Day = Math.Clamp(s.Day ?? 1, 1, maxDay), Cancelled = StayCancelled(s.Status),
            })
            .Concat(taxis.Select(t => new ExpenseLinkOptionDto
            {
                Kind = ExpenseLinkKinds.Taxi, Id = t.Id, Label = TripExpenseRules.TaxiLabel(t.DriverName, t.VehicleName),
                Day = TripExpenseRules.DayOfDate(startDate, t.DateFrom, dayCount), Cancelled = LinkCancelled(ExpenseLinkKinds.Taxi, t.Status),
            }))
            .ToList();
        return new Block(suggestions, links);
    }

    internal sealed class StayRow
    {
        public Guid Id { get; set; }
        public string ListingName { get; set; } = string.Empty;
        public int? Day { get; set; }
        public int? AgreedTotal { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool Skipped { get; set; }
        public bool Linked { get; set; }
    }

    internal sealed class TaxiRow
    {
        public Guid Id { get; set; }
        public string DriverName { get; set; } = string.Empty;
        public string VehicleName { get; set; } = string.Empty;
        public DateOnly DateFrom { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
