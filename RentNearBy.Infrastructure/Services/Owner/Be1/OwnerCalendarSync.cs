using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be1;

// Auto entries follow their source (contract 7.4, 8.1, AU-10, R-1): one read of the source facts, then ONE statement. A confirmed
// source upserts its row by the unique (SourceType, SourceId); any other state cancels an existing row and keeps its money notes.
// Singleton and stateless: it runs on the caller's context inside the caller's transaction, takes no listing lock (Auto rows are
// never counted) and never touches the money columns, the label of an existing row or its traveller link. Every value is checked
// before the write (FK targets by EXISTS, lengths cut, dates ordered), so a calendar rule never fails a booking transaction.
public sealed class OwnerCalendarSync : IOwnerCalendarSync
{
    public static readonly string EnquiryFactsSql = $"""
        SELECT e."Id", e."Kind", e."Status", e."TripId", e."Day", e."ListingId", e."OwnerUserId", e."TravellerUserId", e."CheckIn", e."CheckOut",
               e."DaysJson"::text AS "DaysJson", e."ContextJson"::text AS "ContextJson", e."AgreedTotal", e."ListedStayTotal", e."FailReason",
               e."CancelledByUserId", e."CancelledAt", e."UpdatedAt", e."PickupPlace",
               o."LinesJson"::text AS "OfferLinesJson", u."Name" AS "TravellerName", t."StartDate" AS "TripStart"
        FROM "TripEnquiries" e
        LEFT JOIN "TripOffers" o ON o."Id" = e."AcceptedOfferId"
        LEFT JOIN "Users" u ON u."Id" = e."TravellerUserId"
        LEFT JOIN "Trips" t ON t."Id" = e."TripId"
        WHERE e."Id" = @id
        """;

    public const string BookingFactsSql = """
        SELECT b."Id", b."Status", b."DriverListingId", b."DriverUserId", b."TravellerUserId", b."DateFrom", b."DateTo", b."CancelledBy",
               b."CancelledAt", b."BookedAt", b."CompletedAt", b."PickupCityName", b."Seats", b."TravellerName",
               r."DropCityName", r."DropSameAsPickup", r."Travellers", r."TripSnapshotJson"::text AS "TripSnapshotJson",
               r."SightseeingDaysJson"::text AS "SightseeingDaysJson"
        FROM "TaxiBookings" b
        LEFT JOIN "TaxiRequests" r ON r."Id" = b."RequestId"
        WHERE b."Id" = @id
        """;

    // Insert, or move the row to the source's current facts; Version moves only when something differs.
    public const string UpsertSql = """
        INSERT INTO "CalendarEntries" AS c ("Id", "OwnerUserId", "ListingKind", "ListingId", "Kind", "SourceType", "SourceId", "TravellerUserId",
            "DateFrom", "DateTo", "FromPlace", "ToPlace", "Label", "Detail", "ListedTotal", "AgreedTotal", "Guests", "Seats", "RoomNames", "PlacesJson",
            "Cancelled", "Version", "CreatedAt", "UpdatedAt")
        SELECT gen_random_uuid(), @owner, @k, @l, 'Auto', @st, @src, (SELECT tu."Id" FROM "Users" tu WHERE tu."Id" = @traveller),
            @from, @to, @fromPlace, @toPlace, @label, @detail, @listed, @agreed, @guests, @seats, @rooms, @places,
            false, 1, now(), now()
        WHERE EXISTS (SELECT 1 FROM "Users" ou WHERE ou."Id" = @owner)
        ON CONFLICT ("SourceType", "SourceId") WHERE "Kind" = 'Auto' DO UPDATE SET
            "DateFrom" = EXCLUDED."DateFrom", "DateTo" = EXCLUDED."DateTo", "FromPlace" = EXCLUDED."FromPlace", "ToPlace" = EXCLUDED."ToPlace",
            "Detail" = EXCLUDED."Detail", "AgreedTotal" = EXCLUDED."AgreedTotal", "Guests" = EXCLUDED."Guests", "Seats" = EXCLUDED."Seats",
            "RoomNames" = EXCLUDED."RoomNames", "PlacesJson" = EXCLUDED."PlacesJson",
            "Cancelled" = false, "CancelledBy" = NULL, "CancelledOn" = NULL, "Version" = c."Version" + 1, "UpdatedAt" = now()
        WHERE (c."DateFrom", c."DateTo", c."FromPlace", c."ToPlace", c."Detail", c."AgreedTotal", c."Guests", c."Seats", c."RoomNames", c."PlacesJson", c."Cancelled")
              IS DISTINCT FROM
              (EXCLUDED."DateFrom", EXCLUDED."DateTo", EXCLUDED."FromPlace", EXCLUDED."ToPlace", EXCLUDED."Detail", EXCLUDED."AgreedTotal",
               EXCLUDED."Guests", EXCLUDED."Seats", EXCLUDED."RoomNames", EXCLUDED."PlacesJson", false)
        """;

    public const string CancelSql = """
        UPDATE "CalendarEntries" AS c SET "Cancelled" = true, "CancelledBy" = @by, "CancelledOn" = @on, "Version" = c."Version" + 1, "UpdatedAt" = now()
        WHERE c."SourceType" = @st AND c."SourceId" = @src AND c."Kind" = 'Auto' AND NOT c."Cancelled"
        """;

    public const string ManualDatesSql = """
        SELECT DISTINCT c."DateFrom" AS "Value" FROM "CalendarEntries" c
        WHERE c."ListingKind" = 'Taxi' AND c."ListingId" = @l AND c."Kind" = 'Manual' AND c."DateTo" >= @from AND c."DateFrom" <= @to
        ORDER BY 1
        """;

    public async Task<int> SyncEnquiryAsync(ApplicationDbContext db, Guid enquiryId, CancellationToken ct = default)
    {
        var facts = await db.Database.SqlQueryRaw<EnquiryFacts>(EnquiryFactsSql, Uuid("id", enquiryId)).ToListAsync(ct);
        if (facts.Count == 0 || CalendarAutoText.ForEnquiry(facts[0]) is not { } plan) return 0;
        return await WriteAsync(db, plan, ct);
    }

    public async Task<int> SyncTaxiBookingAsync(ApplicationDbContext db, Guid bookingId, CancellationToken ct = default)
    {
        var facts = await db.Database.SqlQueryRaw<BookingFacts>(BookingFactsSql, Uuid("id", bookingId)).ToListAsync(ct);
        if (facts.Count == 0) return 0;
        return await WriteAsync(db, CalendarAutoText.ForBooking(facts[0]), ct);
    }

    public async Task<IReadOnlyList<DateOnly>> ManualDatesAsync(ApplicationDbContext db, Guid taxiListingId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from) return [];
        return await db.Database.SqlQueryRaw<DateOnly>(ManualDatesSql, Uuid("l", taxiListingId), Date("from", from), Date("to", to)).ToListAsync(ct);
    }

    public static Task<int> WriteAsync(ApplicationDbContext db, AutoEntryPlan plan, CancellationToken ct)
    {
        if (!plan.Confirmed)
            return db.Database.ExecuteSqlRawAsync(CancelSql,
                [Text("st", plan.SourceType), Uuid("src", plan.SourceId), Text("by", plan.CancelledBy ?? CalendarCancelledBy.System), Date("on", plan.CancelledOn)], ct);
        if (plan.OwnerUserId is not { } owner || plan.DateTo < plan.DateFrom) return Task.FromResult(0);
        return db.Database.ExecuteSqlRawAsync(UpsertSql,
        [
            Uuid("owner", owner), Text("k", plan.ListingKind), Uuid("l", plan.ListingId), Text("st", plan.SourceType), Uuid("src", plan.SourceId),
            Uuid("traveller", plan.TravellerUserId), Date("from", plan.DateFrom), Date("to", plan.DateTo),
            Text("fromPlace", plan.FromPlace), Text("toPlace", plan.ToPlace), Text("label", plan.Label), Text("detail", plan.Detail),
            Int("listed", plan.ListedTotal), Int("agreed", plan.AgreedTotal), Small("guests", plan.Guests), Small("seats", plan.Seats),
            Text("rooms", plan.RoomNames), Jsonb("places", plan.PlacesJson),
        ], ct);
    }
}
