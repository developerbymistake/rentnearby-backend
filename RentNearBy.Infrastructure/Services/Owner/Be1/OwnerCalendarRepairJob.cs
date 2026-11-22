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
// same sync the hooks use, so the repaired row is identical to a hooked one. Normally both sets are empty. Each statement walks its
// set by id from a cursor kept between runs (back to the start after a short page), so a source the sync cannot write never
// starves the others; such a source counts as skipped, not fixed.
// FB-31: the 15-minute run reads only sources changed in the last RecentDays, driven from the source side on partial indexes
// (ix_tripenquiries_confirmed_updated, ix_tripenquiries_closed_updated, ix_taxibookings_booked, ix_taxibookings_cancelled);
// the four unbounded statements run once a night in the retention window (OwnerCalendarRepairNightlyJob).
public sealed class OwnerCalendarRepairJob(
    ApplicationDbContext db, IOwnerCalendarSync sync, IOptions<OwnerFeatureOptions> options, TimeProvider clock, ILogger<OwnerCalendarRepairJob> logger)
    : IBuddyFeatureJob
{
    public const int Batch = 500;
    public const int RecentDays = 2;

    private static readonly string LeadOrStay =
        $"""(e."Kind" = '{TripEnquiryKinds.Stay}' OR (e."Kind" = '{TripEnquiryKinds.Taxi}' AND e."TripId" IS NOT NULL AND e."Day" IS NULL))""";

    public static readonly string MissingEnquiriesSql = $"""
        SELECT e."Id" AS "Value" FROM "TripEnquiries" e
        WHERE e."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}' AND {LeadOrStay}
          AND e."OwnerUserId" IS NOT NULL AND e."CheckIn" IS NOT NULL AND COALESCE(e."CheckOut", e."CheckIn") > @cutoff
          AND (e."Kind" <> '{TripEnquiryKinds.Stay}' OR e."CheckOut" > e."CheckIn")
          AND e."Id" > @after
          AND NOT EXISTS (SELECT 1 FROM "CalendarEntries" c
                          WHERE c."SourceType" IN ('{CalendarSourceTypes.StayEnquiry}', '{CalendarSourceTypes.TaxiLead}') AND c."SourceId" = e."Id" AND c."Kind" = 'Auto')
        ORDER BY e."Id"
        LIMIT @n
        """;

    public static readonly string StaleEnquiriesSql = $"""
        SELECT c."SourceId" AS "Value" FROM "CalendarEntries" c
        JOIN "TripEnquiries" e ON e."Id" = c."SourceId"
        WHERE c."Kind" = 'Auto' AND c."SourceType" IN ('{CalendarSourceTypes.StayEnquiry}', '{CalendarSourceTypes.TaxiLead}') AND NOT c."Cancelled"
          AND e."Status" <> '{TripEnquiryStatuses.ConfirmedByBoth}' AND c."SourceId" > @after
        ORDER BY c."SourceId"
        LIMIT @n
        """;

    public static readonly string MissingBookingsSql = $"""
        SELECT b."Id" AS "Value" FROM "TaxiBookings" b
        WHERE b."Status" IN ({BookingStates.ActiveSqlList}, '{BookingStates.Completed}') AND b."DateTo" >= @cutoff
          AND b."Id" > @after
          AND NOT EXISTS (SELECT 1 FROM "CalendarEntries" c WHERE c."SourceType" = '{CalendarSourceTypes.TaxiBooking}' AND c."SourceId" = b."Id" AND c."Kind" = 'Auto')
        ORDER BY b."Id"
        LIMIT @n
        """;

    public static readonly string StaleBookingsSql = $"""
        SELECT c."SourceId" AS "Value" FROM "CalendarEntries" c
        JOIN "TaxiBookings" b ON b."Id" = c."SourceId"
        WHERE c."Kind" = 'Auto' AND c."SourceType" = '{CalendarSourceTypes.TaxiBooking}' AND NOT c."Cancelled" AND b."Status" = '{BookingStates.Cancelled}'
          AND c."SourceId" > @after
        ORDER BY c."SourceId"
        LIMIT @n
        """;

    // Recent path: the same sets, limited to sources whose confirm / close / booking / cancel happened since @since.
    public static readonly string MissingEnquiriesRecentSql = MissingEnquiriesSql.Replace(
        "AND e.\"Id\" > @after", "AND e.\"UpdatedAt\" >= @since AND e.\"Id\" > @after");

    public static readonly string StaleEnquiriesRecentSql = $"""
        SELECT e."Id" AS "Value" FROM "TripEnquiries" e
        WHERE e."UpdatedAt" >= @since AND {RetentionSql.ClosedEnquiry.Replace("\"Status\"", "e.\"Status\"").Replace("\"FailReason\"", "e.\"FailReason\"")}
          AND e."Id" > @after
          AND EXISTS (SELECT 1 FROM "CalendarEntries" c
                      WHERE c."SourceType" IN ('{CalendarSourceTypes.StayEnquiry}', '{CalendarSourceTypes.TaxiLead}') AND c."SourceId" = e."Id"
                        AND c."Kind" = 'Auto' AND NOT c."Cancelled")
        ORDER BY e."Id"
        LIMIT @n
        """;

    public static readonly string MissingBookingsRecentSql = MissingBookingsSql.Replace(
        "AND b.\"Id\" > @after", "AND b.\"BookedAt\" >= @since AND b.\"Id\" > @after");

    public static readonly string StaleBookingsRecentSql = $"""
        SELECT b."Id" AS "Value" FROM "TaxiBookings" b
        WHERE b."Status" = '{BookingStates.Cancelled}' AND b."CancelledAt" >= @since AND b."Id" > @after
          AND EXISTS (SELECT 1 FROM "CalendarEntries" c
                      WHERE c."SourceType" = '{CalendarSourceTypes.TaxiBooking}' AND c."SourceId" = b."Id" AND c."Kind" = 'Auto' AND NOT c."Cancelled")
        ORDER BY b."Id"
        LIMIT @n
        """;

    public string Name => "owner.calendar.repair";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, options.Value.RepairMinutes));

    public TimeOnly? DailyAtIst => null;

    public Task<int> RunAsync(CancellationToken ct) => RepairAsync(full: false, ct);

    public async Task<int> RepairAsync(bool full, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = IstTime.TodayIst(now).AddDays(-OwnerLimits.CalendarDays);
        var since = now.AddDays(-RecentDays);
        var enquiries = (await PageAsync(full ? MissingEnquiriesSql : MissingEnquiriesRecentSql, cutoff, since, ct))
            .Concat(await PageAsync(full ? StaleEnquiriesSql : StaleEnquiriesRecentSql, cutoff, since, ct)).Distinct().ToList();
        var bookings = (await PageAsync(full ? MissingBookingsSql : MissingBookingsRecentSql, cutoff, since, ct))
            .Concat(await PageAsync(full ? StaleBookingsSql : StaleBookingsRecentSql, cutoff, since, ct)).Distinct().ToList();

        int fixedEnquiries = 0, fixedBookings = 0;
        foreach (var id in enquiries) // batch-loop
            if (await sync.SyncEnquiryAsync(db, id, ct) > 0) fixedEnquiries++; // batch-loop
        foreach (var id in bookings) // batch-loop
            if (await sync.SyncTaxiBookingAsync(db, id, ct) > 0) fixedBookings++; // batch-loop

        var skipped = enquiries.Count + bookings.Count - fixedEnquiries - fixedBookings;
        if (fixedEnquiries + fixedBookings > 0)
            logger.LogWarning("owner.calendar.repair fixed {Enquiries} enquiry and {Bookings} booking calendar rows", fixedEnquiries, fixedBookings);
        if (skipped > 0) logger.LogDebug("owner.calendar.repair skipped {Skipped} sources with nothing to write", skipped);
        return fixedEnquiries + fixedBookings;
    }

    private async Task<List<Guid>> PageAsync(string sql, DateOnly cutoff, DateTime since, CancellationToken ct)
    {
        var after = RepairCursors.Get(sql);
        object[] args = sql.Contains("@since", StringComparison.Ordinal)
            ? [Date("cutoff", cutoff), Uuid("after", after), Int("n", Batch), new Npgsql.NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = since }]
            : [Date("cutoff", cutoff), Uuid("after", after), Int("n", Batch)];
        var ids = await db.Database.SqlQueryRaw<Guid>(sql, args).ToListAsync(ct);
        RepairCursors.Set(sql, NextCursor(ids, Batch));
        return ids;
    }

    // Pure part, unit tested: a full page moves the cursor to its last id, a short page starts the next run from the beginning.
    public static Guid NextCursor(IReadOnlyList<Guid> page, int batch) => page.Count >= batch ? page[^1] : Guid.Empty;

    private static class RepairCursors
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> Cursors = new();

        public static Guid Get(string sql) => Cursors.TryGetValue(sql, out var id) ? id : Guid.Empty;

        public static void Set(string sql, Guid id) => Cursors[sql] = id;
    }
}

// FB-31: the full drift net, once a night inside the retention window (01:00 to 04:30 IST).
public sealed class OwnerCalendarRepairNightlyJob(OwnerCalendarRepairJob repair) : IBuddyFeatureJob
{
    public static readonly TimeOnly At = new(2, 40);

    public string Name => "owner.calendar.repair.nightly";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public TimeOnly? DailyAtIst => At;

    public Task<int> RunAsync(CancellationToken ct) => repair.RepairAsync(full: true, ct);
}
