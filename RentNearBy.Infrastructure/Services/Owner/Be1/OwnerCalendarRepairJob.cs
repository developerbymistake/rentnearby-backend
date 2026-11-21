using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be1;

// owner.calendar.repair (contract 5.1, 8.1): the drift net behind the save hook and the three taxi booking calls. Two set-based
// statements per source type find (a) confirmed sources without an Auto row and (b) live Auto rows whose source is no longer
// confirmed, at most 500 each, limited to sources the 90-day clean-up has not passed yet. Each id found is then written by the
// same sync the hooks use, so the repaired row is identical to a hooked one. Normally both sets are empty.
public sealed class OwnerCalendarRepairJob(
    ApplicationDbContext db, IOwnerCalendarSync sync, IOptions<OwnerFeatureOptions> options, TimeProvider clock, ILogger<OwnerCalendarRepairJob> logger)
    : IBuddyFeatureJob
{
    public const int Batch = 500;

    private static readonly string LeadOrStay =
        $"""(e."Kind" = '{TripEnquiryKinds.Stay}' OR (e."Kind" = '{TripEnquiryKinds.Taxi}' AND e."TripId" IS NOT NULL AND e."Day" IS NULL))""";

    public static readonly string MissingEnquiriesSql = $"""
        SELECT e."Id" AS "Value" FROM "TripEnquiries" e
        WHERE e."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}' AND {LeadOrStay}
          AND e."OwnerUserId" IS NOT NULL AND e."CheckIn" IS NOT NULL AND COALESCE(e."CheckOut", e."CheckIn") > @cutoff
          AND (e."Kind" <> '{TripEnquiryKinds.Stay}' OR e."CheckOut" > e."CheckIn")
          AND NOT EXISTS (SELECT 1 FROM "CalendarEntries" c
                          WHERE c."SourceType" IN ('{CalendarSourceTypes.StayEnquiry}', '{CalendarSourceTypes.TaxiLead}') AND c."SourceId" = e."Id" AND c."Kind" = 'Auto')
        LIMIT @n
        """;

    public static readonly string StaleEnquiriesSql = $"""
        SELECT c."SourceId" AS "Value" FROM "CalendarEntries" c
        JOIN "TripEnquiries" e ON e."Id" = c."SourceId"
        WHERE c."Kind" = 'Auto' AND c."SourceType" IN ('{CalendarSourceTypes.StayEnquiry}', '{CalendarSourceTypes.TaxiLead}') AND NOT c."Cancelled"
          AND e."Status" <> '{TripEnquiryStatuses.ConfirmedByBoth}'
        LIMIT @n
        """;

    public static readonly string MissingBookingsSql = $"""
        SELECT b."Id" AS "Value" FROM "TaxiBookings" b
        WHERE b."Status" IN ({BookingStates.ActiveSqlList}, '{BookingStates.Completed}') AND b."DateTo" >= @cutoff
          AND NOT EXISTS (SELECT 1 FROM "CalendarEntries" c WHERE c."SourceType" = '{CalendarSourceTypes.TaxiBooking}' AND c."SourceId" = b."Id" AND c."Kind" = 'Auto')
        LIMIT @n
        """;

    public static readonly string StaleBookingsSql = $"""
        SELECT c."SourceId" AS "Value" FROM "CalendarEntries" c
        JOIN "TaxiBookings" b ON b."Id" = c."SourceId"
        WHERE c."Kind" = 'Auto' AND c."SourceType" = '{CalendarSourceTypes.TaxiBooking}' AND NOT c."Cancelled" AND b."Status" = '{BookingStates.Cancelled}'
        LIMIT @n
        """;

    public string Name => "owner.calendar.repair";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, options.Value.RepairMinutes));

    public TimeOnly? DailyAtIst => null;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var cutoff = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime).AddDays(-OwnerLimits.CalendarDays);
        var enquiries = (await IdsAsync(MissingEnquiriesSql, cutoff, ct)).Concat(await IdsAsync(StaleEnquiriesSql, cutoff, ct)).Distinct().ToList();
        var bookings = (await IdsAsync(MissingBookingsSql, cutoff, ct)).Concat(await IdsAsync(StaleBookingsSql, cutoff, ct)).Distinct().ToList();

        foreach (var id in enquiries) // batch-loop
            await sync.SyncEnquiryAsync(db, id, ct); // batch-loop
        foreach (var id in bookings) // batch-loop
            await sync.SyncTaxiBookingAsync(db, id, ct); // batch-loop

        var total = enquiries.Count + bookings.Count;
        if (total > 0) logger.LogWarning("owner.calendar.repair fixed {Enquiries} enquiry and {Bookings} booking calendar rows", enquiries.Count, bookings.Count);
        return total;
    }

    private async Task<List<Guid>> IdsAsync(string sql, DateOnly cutoff, CancellationToken ct)
        => await db.Database.SqlQueryRaw<Guid>(sql, Date("cutoff", cutoff), Int("n", Batch)).ToListAsync(ct);
}
