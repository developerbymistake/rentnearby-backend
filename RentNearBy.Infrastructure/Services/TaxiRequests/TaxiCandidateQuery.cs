using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

public sealed record TaxiPoolDriver(Guid DriverListingId, Guid DriverUserId, int Seats, long Pass, long SeenSeq, DateTime? LastOfferedAt, bool Free);

public sealed record TaxiCandidatePair(Guid RequestId, Guid DriverListingId, bool Free);

public sealed record TaxiRemainingCounts(int Free, int Busy);

// MASTER_PLAN 2.5 as SQL (mirrored by the pure TaxiFairQueue). Aliases: l = driver TaxiListings row, r = TaxiRequests row.
public static class TaxiCandidateQuery
{
    // Registered driver: feeds the pickup/drop lists, the sightseeing counts and the create check (never ring readiness).
    public static readonly string ListRule = $"""
        l."Status" = '{TaxiStatuses.Approved}' AND l."IsAvailable" AND NOT l."IsDeleted"
        AND (l."DistrictId" IS NULL OR EXISTS (SELECT 1 FROM "Districts" d WHERE d."Id" = l."DistrictId" AND d."IsActive"))
        """;

    // Reachable by a ring: matcher only.
    public static readonly string RingRule = $"""
        (l."RingReadyAt" IS NULL OR l."RingNotificationsOn")
        AND l."RingMissStreak" < {TaxiDispatchRules.RingMissLimit}
        AND EXISTS (SELECT 1 FROM "DeviceTokens" dt WHERE dt."UserId" = l."UserId" AND dt."IsValid")
        """;

    // Pool member for any request: a listed driver who can be rung.
    public static readonly string StaticRule = $"{ListRule}\nAND {RingRule}";

    public static readonly string RouteRule = """
        EXISTS (SELECT 1 FROM "TaxiRoutes" rt WHERE rt."TaxiListingId" = l."Id" AND rt."ToCityKey" = r."MatchToCityKey"
                AND (r."MatchFromCityKey" IS NULL OR rt."FromCityKey" = r."MatchFromCityKey"))
        """;

    public const string SeatsRule = "l.\"Seats\" >= r.\"Seats\"";

    public const string NotTravellerRule = "l.\"UserId\" <> r.\"TravellerUserId\"";

    public const string NoBlockRule = """
        NOT EXISTS (SELECT 1 FROM "UserBlocks" ub
                    WHERE (ub."BlockerId" = l."UserId" AND ub."BlockedId" = r."TravellerUserId")
                       OR (ub."BlockerId" = r."TravellerUserId" AND ub."BlockedId" = l."UserId"))
        """;

    public const string NoOfferYetRule = """
        NOT EXISTS (SELECT 1 FROM "TaxiRequestOffers" po WHERE po."RequestId" = r."Id" AND po."DriverListingId" = l."Id")
        """;

    public static readonly string NoBookingOverlapRule = $"""
        NOT EXISTS (SELECT 1 FROM "TaxiBookings" bk WHERE bk."DriverListingId" = l."Id" AND bk."Status" IN ({BookingStates.ActiveSqlList})
                    AND bk."DateFrom" <= r."DateTo" AND bk."DateTo" >= r."DateFrom")
        """;

    // Legacy local taxi rows: CheckOut is the day after the last day, so it overlaps when CheckOut > DateFrom.
    public static readonly string NoLegacyOverlapRule = $"""
        NOT EXISTS (SELECT 1 FROM "TripEnquiries" te WHERE te."ListingId" = l."Id" AND te."Kind" = '{TripEnquiryKinds.Taxi}'
                    AND te."TripId" IS NOT NULL AND te."Day" IS NULL AND te."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}'
                    AND te."CheckIn" <= r."DateTo" AND COALESCE(te."CheckOut", te."CheckIn" + 1) > r."DateFrom")
        """;

    // Free now: no ring anywhere (decision 2) and no pending selection anywhere (decision 7); Accepted does not block.
    public static readonly string FreeRule = $"""
        NOT EXISTS (SELECT 1 FROM "TaxiRequestOffers" fo WHERE fo."DriverListingId" = l."Id"
                    AND fo."Status" IN ('{OfferStates.Ringing}', '{OfferStates.Selected}'))
        """;

    public static readonly string RequestRule = string.Join("\nAND ",
        RouteRule, SeatsRule, NotTravellerRule, NoBlockRule, NoOfferYetRule, NoBookingOverlapRule, NoLegacyOverlapRule);

    private static readonly string DispatchingList = string.Join(", ", RequestStates.Dispatching.Select(s => $"'{s}'"));

    // A driver belongs to a city's pool when he can serve the to-city of any request the city is dispatching.
    public static readonly string CityPoolRule = $"""
        EXISTS (SELECT 1 FROM "TaxiRoutes" prt JOIN "TaxiRequests" q ON q."MatchToCityKey" = prt."ToCityKey"
                WHERE prt."TaxiListingId" = l."Id" AND q."DispatchCityKey" = @city AND q."Status" IN ({DispatchingList}))
        """;

    // New state rows start with SeenSeq -1 so the fair queue never counts them as continuing (floor rule, 2.6).
    public static readonly string EnsureDispatchStatesSql = $"""
        INSERT INTO "DriverDispatchStates" ("DriverListingId", "CityKey", "Pass", "SeenSeq")
        SELECT l."Id", @city, 0, -1 FROM "TaxiListings" l
        WHERE {StaticRule}
        AND {CityPoolRule}
        ON CONFLICT ("DriverListingId", "CityKey") DO NOTHING
        """;

    public static readonly string PoolSql = $"""
        SELECT l."Id" AS "DriverListingId", l."UserId" AS "DriverUserId", COALESCE(l."Seats", 0)::int AS "Seats",
               ds."Pass", ds."SeenSeq", ds."LastOfferedAt", ({FreeRule}) AS "Free"
        FROM "DriverDispatchStates" ds
        JOIN "TaxiListings" l ON l."Id" = ds."DriverListingId"
        WHERE ds."CityKey" = @city
        AND {StaticRule}
        AND {CityPoolRule}
        ORDER BY ds."DriverListingId"
        FOR UPDATE OF ds SKIP LOCKED
        """;

    public static readonly string CandidatesSql = $"""
        SELECT r."Id" AS "RequestId", l."Id" AS "DriverListingId", ({FreeRule}) AS "Free"
        FROM "TaxiRequests" r
        JOIN "TaxiListings" l ON {RouteRule}
        WHERE r."Id" = ANY(@ids)
        AND {StaticRule}
        AND {RequestRule}
        """;

    public static readonly string RemainingSql = $"""
        SELECT count(*) FILTER (WHERE x."Free")::int AS "Free", count(*) FILTER (WHERE NOT x."Free")::int AS "Busy"
        FROM (
            SELECT ({FreeRule}) AS "Free"
            FROM "TaxiRequests" r
            JOIN "TaxiListings" l ON {RouteRule}
            WHERE r."Id" = @id
            AND {StaticRule}
            AND {RequestRule}
        ) x
        """;

    // Matcher step before LoadPoolAsync: the pool query takes its lock on the state rows, so they must exist.
    public static Task<int> EnsureDispatchStatesAsync(ApplicationDbContext db, long cityKey, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(EnsureDispatchStatesSql, [City(cityKey)], ct);

    // Pool drivers of the city with their queue position; state rows locked by another pass are skipped.
    public static async Task<IReadOnlyList<TaxiPoolDriver>> LoadPoolAsync(ApplicationDbContext db, long cityKey, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<PoolRow>(PoolSql, City(cityKey)).ToListAsync(ct);
        return rows.Select(r => new TaxiPoolDriver(r.DriverListingId, r.DriverUserId, r.Seats, r.Pass, r.SeenSeq, r.LastOfferedAt, r.Free)).ToList();
    }

    // Every (request, driver) pair that passes all rules of 2.5 for the given requests.
    public static async Task<IReadOnlyList<TaxiCandidatePair>> LoadCandidatesAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> requestIds, CancellationToken ct = default)
    {
        if (requestIds.Count == 0) return [];
        var rows = await db.Database.SqlQueryRaw<PairRow>(CandidatesSql,
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = requestIds.ToArray() }).ToListAsync(ct);
        return rows.Select(r => new TaxiCandidatePair(r.RequestId, r.DriverListingId, r.Free)).ToList();
    }

    // Ask more: drivers left to ask for this request, free now or busy.
    public static async Task<TaxiRemainingCounts> CountRemainingAsync(ApplicationDbContext db, Guid requestId, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<RemainingRow>(RemainingSql,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = requestId }).SingleAsync(ct);
        return new TaxiRemainingCounts(row.Free, row.Busy);
    }

    private static NpgsqlParameter City(long cityKey) => new("city", NpgsqlDbType.Bigint) { Value = cityKey };

    private sealed class PoolRow
    {
        public Guid DriverListingId { get; set; }
        public Guid DriverUserId { get; set; }
        public int Seats { get; set; }
        public long Pass { get; set; }
        public long SeenSeq { get; set; }
        public DateTime? LastOfferedAt { get; set; }
        public bool Free { get; set; }
    }

    private sealed class PairRow
    {
        public Guid RequestId { get; set; }
        public Guid DriverListingId { get; set; }
        public bool Free { get; set; }
    }

    private sealed class RemainingRow
    {
        public int Free { get; set; }
        public int Busy { get; set; }
    }
}
