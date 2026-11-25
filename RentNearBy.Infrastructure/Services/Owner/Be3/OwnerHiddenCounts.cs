using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be3;

// M3 (contract 2.4, AU-18, AU-21): hidden counts and visible usage of the caller's live listing of one kind, plus the payment
// start facts of the flag row, in ONE statement of scalar subqueries (listing UserId index, then the row tables by listing id).
// No listing = zero counts.
public static class OwnerHiddenCounts
{
    public sealed record Counts(PlanHiddenDto Hidden, PlanUsageDto Usage, DateOnly? AnnounceDate = null, DateTime? PaymentEnabledAt = null,
        int InactiveJobs = 0);

    public static readonly Counts None = new(PlanHiddenDto.None, new PlanUsageDto(0, 0, 0));

    private sealed class Row
    {
        public int HiddenPhotos { get; set; }
        public int HiddenRooms { get; set; }
        public int HiddenRoutes { get; set; }
        public int PausedRules { get; set; }
        public int InactiveJobs { get; set; }
        public int Photos { get; set; }
        public int Rooms { get; set; }
        public int Routes { get; set; }
        public DateOnly? AnnounceDate { get; set; }
        public DateTime? PaymentEnabledAt { get; set; }
    }

    // The payment flag row is read once for both columns.
    private const string Flag = """
          pf."AnnounceDate", pf."PaymentEnabledAt"
        """;

    private static readonly string FlagJoin = $"""
        LEFT JOIN LATERAL (SELECT f."AnnounceDate", f."PaymentEnabledAt" FROM "AppFeatureFlags" f
                           WHERE f."FeatureKey" = '{AppFeatureKeys.PaymentEnabled}' LIMIT 1) pf ON true
        """;

    public static readonly string StaySql = $"""
        SELECT
          (SELECT count(*) FROM "StayPhotos" p WHERE p."StayListingId" = l."Id" AND p."HiddenByPlan")::int AS "HiddenPhotos",
          (SELECT count(*) FROM "StayRooms" r WHERE r."StayListingId" = l."Id" AND r."HiddenByPlan")::int AS "HiddenRooms",
          0 AS "HiddenRoutes",
          (SELECT count(*) FROM "StaySpecialPrices" sp WHERE sp."StayListingId" = l."Id" AND sp."PausedByPlan" AND sp."DateTo" >= @today)::int AS "PausedRules",
          (SELECT count(*) FROM "StayJobs" j WHERE l."Id" IS NOT NULL AND j."UserId" = @me AND j."Status" = 'Inactive')::int AS "InactiveJobs",
          (SELECT count(*) FROM "StayPhotos" p WHERE p."StayListingId" = l."Id" AND NOT p."HiddenByPlan")::int AS "Photos",
          (SELECT count(*) FROM "StayRooms" r WHERE r."StayListingId" = l."Id" AND NOT r."HiddenByPlan")::int AS "Rooms",
          0 AS "Routes",
        {Flag}
        FROM (SELECT 1) one
        LEFT JOIN LATERAL (SELECT s."Id" FROM "StayListings" s WHERE s."UserId" = @me AND s."IsDeleted" = false LIMIT 1) l ON true
        {FlagJoin}
        """;

    public static readonly string TaxiSql = $"""
        SELECT 0 AS "HiddenPhotos", 0 AS "HiddenRooms",
          (SELECT count(*) FROM "TaxiRoutes" r WHERE r."TaxiListingId" = l."Id" AND r."HiddenByPlan")::int AS "HiddenRoutes",
          0 AS "PausedRules", 0 AS "InactiveJobs", 0 AS "Photos", 0 AS "Rooms",
          (SELECT count(*) FROM "TaxiRoutes" r WHERE r."TaxiListingId" = l."Id" AND NOT r."HiddenByPlan")::int AS "Routes",
        {Flag}
        FROM (SELECT 1) one
        LEFT JOIN LATERAL (SELECT t."Id" FROM "TaxiListings" t WHERE t."UserId" = @me AND t."IsDeleted" = false LIMIT 1) l ON true
        {FlagJoin}
        """;

    public static async Task<Counts> ReadAsync(ApplicationDbContext db, Guid userId, string kind, DateOnly todayIst, CancellationToken ct = default)
    {
        var sql = kind switch
        {
            ListingKinds.Stay => StaySql,
            ListingKinds.Taxi => TaxiSql,
            _ => null,
        };
        if (sql == null) return None;
        var rows = await db.Database.SqlQueryRaw<Row>(sql,
            new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = userId },
            new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = todayIst }).ToListAsync(ct);
        if (rows.Count == 0) return None;
        var r = rows[0];
        return new Counts(new PlanHiddenDto(r.HiddenPhotos, r.HiddenRooms, r.HiddenRoutes, r.PausedRules),
            new PlanUsageDto(r.Photos, r.Rooms, r.Routes), r.AnnounceDate, r.PaymentEnabledAt, r.InactiveJobs);
    }

    // The PlanLimitsChanged notice goes once nothing of the stay is hidden, paused or inactive by the plan any more.
    public static async Task ClearStayNoticeIfNothingHiddenAsync(ApplicationDbContext db, IOwnerNotifier notifier, Guid userId, CancellationToken ct = default)
    {
        var counts = await ReadAsync(db, userId, ListingKinds.Stay, IstTime.TodayIst(DateTime.UtcNow), ct);
        if (counts.Hidden.Total + counts.InactiveJobs == 0)
            await notifier.ClearAsync(NoticeKinds.PlanLimitsChanged, NoticeKinds.NoTrip, [userId], ct);
    }

    // AU-18: the announced start while payment is OFF and the date is ahead; "plans started today" on the IST day of the switch.
    public static DateOnly? PaymentStartsOn(bool paymentEnabled, DateOnly? announceDate, DateOnly todayIst)
        => !paymentEnabled && announceDate is { } d && d > todayIst ? d : null;

    public static bool PlansStartedToday(bool paymentEnabled, DateTime? paymentEnabledAt, DateOnly todayIst)
        => paymentEnabled && paymentEnabledAt is { } at && IstTime.TodayIst(at) == todayIst;

    public static IReadOnlyList<MeterDto> StayMeters(PlanUsageDto usage, PlanLimits limits) =>
    [
        new(PlanFeatureKeys.Photos, usage.Photos, limits.Photos),
        new(PlanFeatureKeys.RoomTypes, usage.RoomTypes, limits.RoomTypes),
    ];

    public static IReadOnlyList<MeterDto> TaxiMeters(PlanUsageDto usage, PlanLimits limits) =>
        [new(PlanFeatureKeys.Routes, usage.Routes, limits.Routes)];
}
