using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Owner.Be4.OwnerJobSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// Admin reads of this work (section 9, AU-24): rows hidden by the plan with their flag (A4), hidden and visible counts (A6),
// the special prices report (A5) and the payment switch preview (A7). Never a calendar row, money note or block reason (A8).
public sealed class AdminOwnerReads(ApplicationDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A4: every photo and room of a stay, hidden ones too, each with hiddenByPlan (one statement).
    internal const string StayRowsSql = """
        SELECT
          (SELECT COALESCE(json_agg(json_build_object('id', p."Id", 'url', p."PhotoUrl", 'photoOrder', p."PhotoOrder", 'hiddenByPlan', p."HiddenByPlan")
                                    ORDER BY p."HiddenByPlan", p."PhotoOrder", p."Id"), '[]'::json)
           FROM "StayPhotos" p WHERE p."StayListingId" = @id)::text AS "Photos",
          (SELECT COALESCE(json_agg(json_build_object('id', r."Id", 'roomName', r."RoomName", 'pricePerNight', r."PricePerNight", 'maxGuests', r."MaxGuests",
                                                      'meals', r."Meals", 'hiddenByPlan', r."HiddenByPlan") ORDER BY r."HiddenByPlan", r."RoomIndex", r."Id"), '[]'::json)
           FROM "StayRooms" r WHERE r."StayListingId" = @id)::text AS "Rooms"
        """;

    internal const string TaxiRowsSql = """
        SELECT COALESCE(json_agg(json_build_object('id', r."Id", 'fromName', r."FromName", 'toName', r."ToName", 'fare', r."Fare",
                                                   'tripType', r."TripType", 'hiddenByPlan', r."HiddenByPlan") ORDER BY r."HiddenByPlan", r."RouteIndex", r."Id"), '[]'::json)::text AS "Value"
        FROM "TaxiRoutes" r WHERE r."TaxiListingId" = @id
        """;

    // A6 / M3 shape: hidden and visible counts of the user's live listing of the kind (one statement).
    internal const string CountsSql = """
        SELECT
          COALESCE((SELECT count(*) FROM "StayPhotos" p JOIN "StayListings" s ON s."Id" = p."StayListingId"
                    WHERE s."UserId" = @user AND NOT s."IsDeleted" AND p."HiddenByPlan"), 0)::int AS "HiddenPhotos",
          COALESCE((SELECT count(*) FROM "StayRooms" r JOIN "StayListings" s ON s."Id" = r."StayListingId"
                    WHERE s."UserId" = @user AND NOT s."IsDeleted" AND r."HiddenByPlan"), 0)::int AS "HiddenRooms",
          COALESCE((SELECT count(*) FROM "TaxiRoutes" t JOIN "TaxiListings" l ON l."Id" = t."TaxiListingId"
                    WHERE l."UserId" = @user AND NOT l."IsDeleted" AND t."HiddenByPlan"), 0)::int AS "HiddenRoutes",
          COALESCE((SELECT count(*) FROM "StaySpecialPrices" sp JOIN "StayListings" s ON s."Id" = sp."StayListingId"
                    WHERE s."UserId" = @user AND NOT s."IsDeleted" AND sp."PausedByPlan" AND sp."DateTo" >= @today), 0)::int AS "HiddenSpecial",
          COALESCE((SELECT count(*) FROM "StayPhotos" p JOIN "StayListings" s ON s."Id" = p."StayListingId"
                    WHERE s."UserId" = @user AND NOT s."IsDeleted" AND NOT p."HiddenByPlan"), 0)::int AS "Photos",
          COALESCE((SELECT count(*) FROM "StayRooms" r JOIN "StayListings" s ON s."Id" = r."StayListingId"
                    WHERE s."UserId" = @user AND NOT s."IsDeleted" AND NOT r."HiddenByPlan"), 0)::int AS "Rooms",
          COALESCE((SELECT count(*) FROM "TaxiRoutes" t JOIN "TaxiListings" l ON l."Id" = t."TaxiListingId"
                    WHERE l."UserId" = @user AND NOT l."IsDeleted" AND NOT t."HiddenByPlan"), 0)::int AS "Routes"
        """;

    // A5 report: active rules (not ended, not paused, not paused by the plan), keyset (DateFrom, Id), 20 a page.
    internal static string ReportSql(bool bigOffOnly, bool after) => $"""
        SELECT r."Id" AS "RuleId", r."StayListingId" AS "StayId", s."Name" AS "StayName", r."DateFrom", r."DateTo", r."Direction", r."Percent"
        FROM "StaySpecialPrices" r
        JOIN "StayListings" s ON s."Id" = r."StayListingId"
        WHERE r."DateTo" >= @today AND NOT r."IsPaused" AND NOT r."PausedByPlan" AND NOT s."IsDeleted"
          {(bigOffOnly ? $"AND r.\"Direction\" = '{SpecialPriceDirections.Lower}' AND r.\"Percent\" >= {OwnerLimits.SpecialBigOffPercent}" : string.Empty)}
          {(after ? "AND (r.\"DateFrom\", r.\"Id\") > (@afterDate, @afterId)" : string.Empty)}
        ORDER BY r."DateFrom", r."Id"
        LIMIT @take
        """;

    // A5 list column: the biggest active "Lower" percent per stay of a page (one grouped statement).
    internal static readonly string MaxLowerSql = $"""
        SELECT r."StayListingId" AS "StayId", max(r."Percent")::int AS "Percent"
        FROM "StaySpecialPrices" r
        WHERE r."StayListingId" = ANY(@ids) AND r."Direction" = '{SpecialPriceDirections.Lower}' AND r."DateTo" >= @today
          AND NOT r."IsPaused" AND NOT r."PausedByPlan"
        GROUP BY r."StayListingId"
        """;

    // A7 preview: owners who would lose something if payment went ON now (visible rows above the Free value, or active special
    // prices above the Free value). One statement over the listings; Free values are inline numbers from the catalog.
    internal const string PreviewSql = """
        SELECT
          (SELECT count(*) FROM "StayListings" s
           WHERE NOT s."IsDeleted" AND s."Status" <> 'Draft'
             AND NOT EXISTS (SELECT 1 FROM "UserPlans" u WHERE u."UserId" = s."UserId" AND u."FeatureKey" = 'Stay' AND u."Status" = 'Active' AND u."EndsAt" > now())
             AND ((SELECT count(*) FROM "StayPhotos" p WHERE p."StayListingId" = s."Id" AND NOT p."HiddenByPlan") > @freePhotos
               OR (SELECT count(*) FROM "StayRooms" r WHERE r."StayListingId" = s."Id" AND NOT r."HiddenByPlan") > @freeRooms
               OR (SELECT count(*) FROM "StaySpecialPrices" sp WHERE sp."StayListingId" = s."Id" AND sp."DateTo" >= @today) > @freeSpecial))
          +
          (SELECT count(*) FROM "TaxiListings" l
           WHERE NOT l."IsDeleted" AND l."Status" <> 'Draft'
             AND NOT EXISTS (SELECT 1 FROM "UserPlans" u WHERE u."UserId" = l."UserId" AND u."FeatureKey" = 'Taxi' AND u."Status" = 'Active' AND u."EndsAt" > now())
             AND (SELECT count(*) FROM "TaxiRoutes" t WHERE t."TaxiListingId" = l."Id" AND NOT t."HiddenByPlan") > @freeRoutes) AS "Value"
        """;

    public async Task<AdminPlanRowsDto> StayRowsAsync(Guid stayId, CancellationToken ct = default)
    {
        var row = (await db.Database.SqlQueryRaw<StayRowsRow>(StayRowsSql, Uuid("id", stayId)).ToListAsync(ct)).FirstOrDefault();
        return new AdminPlanRowsDto(Parse<AdminPlanPhotoDto>(row?.Photos), Parse<AdminPlanRoomDto>(row?.Rooms), []);
    }

    public async Task<AdminPlanRowsDto> TaxiRowsAsync(Guid taxiId, CancellationToken ct = default)
    {
        var json = (await db.Database.SqlQueryRaw<string>(TaxiRowsSql, Uuid("id", taxiId)).ToListAsync(ct)).FirstOrDefault();
        return new AdminPlanRowsDto([], [], Parse<AdminPlanRouteDto>(json));
    }

    public async Task<(OwnerHiddenDto Hidden, AdminPlanUsageDto Usage)> CountsAsync(Guid userId, CancellationToken ct = default)
    {
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var r = (await db.Database.SqlQueryRaw<CountsRow>(CountsSql, Uuid("user", userId), Date("today", today)).ToListAsync(ct)).FirstOrDefault() ?? new CountsRow();
        return (new OwnerHiddenDto(r.HiddenPhotos, r.HiddenRooms, r.HiddenRoutes, r.HiddenSpecial), new AdminPlanUsageDto(r.Photos, r.Rooms, r.Routes));
    }

    public async Task<OwnerPageDto<AdminSpecialPriceReportRowDto>> SpecialPriceReportAsync(
        bool bigOffOnly, (DateOnly Date, Guid Id)? after, CancellationToken ct = default)
    {
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var ps = new List<object> { Date("today", today), Int("take", OwnerLimits.PageSize + 1) };
        if (after is { } k)
        {
            ps.Add(Date("afterDate", k.Date));
            ps.Add(Uuid("afterId", k.Id));
        }
        var rows = await db.Database.SqlQueryRaw<ReportRow>(ReportSql(bigOffOnly, after != null), ps.ToArray()).ToListAsync(ct);
        var page = rows.Take(OwnerLimits.PageSize).Select(r => new AdminSpecialPriceReportRowDto(
            r.RuleId, r.StayId, r.StayName, r.DateFrom, r.DateTo, r.Direction, r.Percent,
            r.Direction == SpecialPriceDirections.Lower && r.Percent >= OwnerLimits.SpecialBigOffPercent)).ToList();
        var next = rows.Count > OwnerLimits.PageSize && page.Count > 0 ? OwnerCursors.EncodeDate(page[^1].DateFrom, page[^1].RuleId) : null;
        return new OwnerPageDto<AdminSpecialPriceReportRowDto>(page, next);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> MaxLowerPercentAsync(IReadOnlyCollection<Guid> stayIds, CancellationToken ct = default)
    {
        if (stayIds.Count == 0) return new Dictionary<Guid, int>();
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var rows = await db.Database.SqlQueryRaw<MaxRow>(MaxLowerSql,
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = stayIds.ToArray() }, Date("today", today)).ToListAsync(ct);
        return rows.ToDictionary(r => r.StayId, r => r.Percent);
    }

    public async Task<int> OwnersAffectedPreviewAsync(PlanCatalogSnapshot snapshot, CancellationToken ct = default)
    {
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var stay = snapshot.DefaultValues(ListingKinds.Stay);
        var taxi = snapshot.DefaultValues(ListingKinds.Taxi);
        var value = (await db.Database.SqlQueryRaw<long>(PreviewSql,
            Int("freePhotos", stay.Photos), Int("freeRooms", stay.RoomTypes), Int("freeSpecial", stay.SpecialPrices),
            Int("freeRoutes", taxi.Routes), Date("today", today)).ToListAsync(ct)).FirstOrDefault();
        return (int)Math.Min(value, int.MaxValue);
    }

    private static List<T> Parse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class StayRowsRow
    {
        public string? Photos { get; set; }
        public string? Rooms { get; set; }
    }

    private sealed class CountsRow
    {
        public int HiddenPhotos { get; set; }
        public int HiddenRooms { get; set; }
        public int HiddenRoutes { get; set; }
        public int HiddenSpecial { get; set; }
        public int Photos { get; set; }
        public int Rooms { get; set; }
        public int Routes { get; set; }
    }

    private sealed class ReportRow
    {
        public Guid RuleId { get; set; }
        public Guid StayId { get; set; }
        public string? StayName { get; set; }
        public DateOnly DateFrom { get; set; }
        public DateOnly DateTo { get; set; }
        public string Direction { get; set; } = string.Empty;
        public short Percent { get; set; }
    }

    private sealed class MaxRow
    {
        public Guid StayId { get; set; }
        public int Percent { get; set; }
    }
}
