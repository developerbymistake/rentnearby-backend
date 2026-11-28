using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Discover travellers (Buddy_resume/discover/DESIGN.md 1-3). The seam lets the handler run without a database in tests.
public interface IDiscoverService
{
    Task<DiscoverPageDto> PageAsync(Guid me, string chip, DiscoverCursorValue? cursor, int limit, bool refresh, CancellationToken ct = default);
    Task HideAsync(Guid me, Guid him, CancellationToken ct = default);
    Task<DiscoverSimilarDto> SimilarAsync(Guid me, Guid him, int limit, CancellationToken ct = default);
}

public sealed record DiscoverScan(IReadOnlyList<DiscoverSnapshotRow> Matched, DiscoverSnapshotRow? LastExamined, bool Exhausted);

// DESIGN 3.3 paging over the snapshot (pure): read 60 below the bound, keep the chip's rows until 30 match, at most 4 reads.
public static class DiscoverPaging
{
    public const int ReadSize = 60;
    public const int HydrateSize = 30;
    public const int MaxReads = 4;

    public static async Task<DiscoverScan> ScanAsync(Func<double?, Task<IReadOnlyList<DiscoverSnapshotRow>>> read, double? start, string chip,
        int readSize = ReadSize, int want = HydrateSize, int maxReads = MaxReads)
    {
        var matched = new List<DiscoverSnapshotRow>();
        DiscoverSnapshotRow? last = null;
        var bound = start;
        for (var i = 0; i < maxReads; i++)
        {
            var batch = await read(bound); // batch-loop: bounded keyset reads of the cached snapshot
            for (var j = 0; j < batch.Count; j++)
            {
                var row = batch[j];
                last = row;
                if (row.Code is { } code && DiscoverRules.Matches(code.Flags, chip)) matched.Add(row);
                if (matched.Count >= want) return new DiscoverScan(matched, last, j == batch.Count - 1 && batch.Count < readSize);
            }
            if (batch.Count < readSize) return new DiscoverScan(matched, last, true);
            bound = last!.Score;
        }
        return new DiscoverScan(matched, last, false);
    }

    // How many survivors to send and the row the next cursor starts below. More survivors than the page: the cursor is the last
    // SENT row (never the last examined one, which would skip the unsent survivors). Else the last examined row, or null at the end.
    public static (int Take, DiscoverSnapshotRow? CursorRow) Cut(IReadOnlyList<DiscoverSnapshotRow> survivors, DiscoverScan scan, int limit)
    {
        if (survivors.Count > limit) return (limit, survivors[limit - 1]);
        return (survivors.Count, scan.Exhausted ? null : scan.LastExamined);
    }

    public static string? Cursor(long stamp, DiscoverSnapshotRow? row) => row is null ? null : DiscoverCursor.Encode(stamp, row.Points, row.UserId);
}

public sealed class DiscoverService(
    ApplicationDbContext db, DiscoverCache cache, ITripCityCatalog catalog, TimeProvider clock, ILogger<DiscoverService> logger) : IDiscoverService
{
    public const int TripCandidatesMax = 300;
    public const int PostsScanMax = 1000;
    public const int TagsShown = 3;
    public const int TagCandidatesMax = 2000;
    public const int SimilarPostsPerCity = 50;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static readonly string Stale = Interval(PostNotifyRules.AreaStaleAfter);
    private static readonly string OpenStatuses = $"('{TripStatuses.Draft}', '{TripStatuses.Planned}')";

    // DESIGN 2.2, applied once in the build and again live at page time: followed or requested, blocked either way, hidden < 30 days.
    internal static string Excluded(string id) => $"""
        NOT EXISTS (SELECT 1 FROM "Follows" xf WHERE xf."FollowerId" = @me AND xf."FolloweeId" = {id})
        AND NOT {BlockedEitherWay("@me", id)}
        AND NOT EXISTS (SELECT 1 FROM "DiscoverHides" xh WHERE xh."UserId" = @me AND xh."HiddenUserId" = {id}
                        AND xh."HiddenAt" > now() - {Interval(DiscoverRules.HideFor)})
        """;

    // My open trips (owned or joined, not over), soonest first, at most 6 (Plus ActiveTrips).
    internal static readonly string MyTripsSql = $"""
        SELECT t."CityKey", t."StartDate", t."EndDate", t."PostStartMonth", t."PublishedAt", t."PostWindowMonths"
        FROM "Trips" t
        WHERE t."Id" IN (SELECT o."Id" FROM "Trips" o WHERE o."OwnerUserId" = @me AND o."Status" IN {OpenStatuses}
                         UNION ALL
                         SELECT m."TripId" FROM "TripMembers" m WHERE m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Joined}')
          AND t."Status" IN {OpenStatuses}
          AND (t."EndDate" >= @today OR t."StartDate" >= @today OR (t."StartDate" IS NULL AND t."EndDate" IS NULL))
        ORDER BY t."StartDate" NULLS LAST, t."UpdatedAt" DESC
        LIMIT 6
        """;

    // Live posts in my trips' cities by public, active owners (a private owner's post is hidden from non-followers). The dates-near
    // test runs in C# (DiscoverRules.DatesNear), so the SQL and the rule can never drift.
    internal static readonly string PostsSql = $"""
        SELECT t."OwnerUserId", t."CityKey", t."StartDate", t."PostDays", t."PostStartMonth", t."PublishedAt", t."PostWindowMonths"
        FROM "Trips" t
        JOIN "Users" o ON o."Id" = t."OwnerUserId" AND {ActiveSeeker("o")}
        JOIN "UserProfiles" op ON op."UserId" = t."OwnerUserId" AND NOT op."IsPrivate"
        WHERE t."CityKey" = ANY(@cities) AND {PublicTripReader.LivePost("t")} AND t."OwnerUserId" <> @me
          AND {Excluded("t.\"OwnerUserId\"")}
        ORDER BY t."PublishedAt" DESC NULLS LAST, t."Id" DESC
        LIMIT @take
        """;

    // One statement for every other source (DESIGN 2.1), each capped; trip candidates come in as arrays.
    internal static readonly string SnapshotSql = $"""
        WITH me AS (
          SELECT p."Interests" AS ints, ARRAY(SELECT lower(x) FROM unnest(p."Places") x) AS places, p."HomeStateName" AS state,
                 CASE WHEN p."LastDistrictAt" > now() - {Stale} THEN p."LastDistrictId" END AS district
          FROM "UserProfiles" p WHERE p."UserId" = @me),
        tc AS (SELECT x.id, x.city, NULLIF(x.month, 0) AS month FROM unnest(@tripIds, @tripCity, @tripMonth) AS x(id, city, month)),
        myall AS (
          (SELECT t."Id", t."CityKey", t."OwnerUserId", t."UpdatedAt" FROM "Trips" t WHERE t."OwnerUserId" = @me
           ORDER BY t."UpdatedAt" DESC LIMIT 100)
          UNION
          (SELECT t."Id", t."CityKey", t."OwnerUserId", t."UpdatedAt" FROM "TripMembers" m JOIN "Trips" t ON t."Id" = m."TripId"
           WHERE m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Joined}' ORDER BY m."JoinedAt" DESC NULLS LAST LIMIT 100)),
        mate AS (
          SELECT y.id, y.city FROM (
            SELECT DISTINCT ON (x.id) x.id, x.city, x.at FROM (
              SELECT m."UserId" AS id, a."CityKey" AS city, a."UpdatedAt" AS at FROM myall a
              JOIN "TripMembers" m ON m."TripId" = a."Id" AND m."Status" = '{TripMemberStatuses.Joined}' AND m."UserId" <> @me
              UNION ALL
              SELECT a."OwnerUserId", a."CityKey", a."UpdatedAt" FROM myall a WHERE a."OwnerUserId" <> @me) x
            ORDER BY x.id, x.at DESC) y
          ORDER BY y.at DESC LIMIT 200),
        fol AS (
          SELECT f."FollowerId" AS id FROM "Follows" f
          WHERE f."FolloweeId" = @me AND f."Status" = '{FollowStatuses.Accepted}'
            AND NOT EXISTS (SELECT 1 FROM "Follows" g WHERE g."FollowerId" = @me AND g."FolloweeId" = f."FollowerId")
          ORDER BY f."CreatedAt" DESC LIMIT 200),
        f1 AS (
          SELECT f."FolloweeId" AS mid FROM "Follows" f
          WHERE f."FollowerId" = @me AND f."Status" = '{FollowStatuses.Accepted}'
          ORDER BY f."CreatedAt" DESC LIMIT 200),
        mut AS (
          SELECT h.id, count(*)::int AS cnt, (array_agg(h.mid ORDER BY h.at DESC))[1] AS first
          FROM (SELECT f2.id, f1.mid, f2.at FROM f1
                JOIN "Users" mu ON mu."Id" = f1.mid AND {ActiveSeeker("mu")}
                CROSS JOIN LATERAL (SELECT f."FolloweeId" AS id, f."CreatedAt" AS at FROM "Follows" f
                                    WHERE f."FollowerId" = f1.mid AND f."Status" = '{FollowStatuses.Accepted}'
                                    ORDER BY f."CreatedAt" DESC LIMIT 200) f2
                WHERE f2.id <> @me AND NOT {BlockedEitherWay("@me", "f1.mid")}
                  AND NOT EXISTS (SELECT 1 FROM "Follows" g WHERE g."FollowerId" = @me AND g."FolloweeId" = f2.id)) h
          GROUP BY h.id
          ORDER BY count(*) DESC, max(h.at) DESC
          LIMIT 400),
        near AS (
          SELECT p."UserId" AS id FROM "UserProfiles" p, me
          WHERE me.district IS NOT NULL AND p."LastDistrictId" = me.district AND p."LastDistrictAt" > now() - {Stale}
            AND NOT p."IsPrivate" AND p."UserId" <> @me
          ORDER BY p."LastDistrictAt" DESC LIMIT 300),
        intr AS (
          SELECT c.id,
                 ARRAY(SELECT (o.n - 1)::int FROM unnest(c.ints) WITH ORDINALITY AS o(v, n) WHERE o.v = ANY(me.ints) ORDER BY o.n) AS iidx,
                 ARRAY(SELECT (o.n - 1)::int FROM unnest(c.places) WITH ORDINALITY AS o(v, n) WHERE lower(o.v) = ANY(me.places) ORDER BY o.n) AS pidx
          FROM (SELECT p."UserId" AS id, p."Interests" AS ints, p."Places" AS places
                FROM "UserProfiles" p, me
                WHERE p."UserId" <> @me AND NOT p."IsPrivate"
                  AND (p."Interests" && me.ints OR EXISTS (SELECT 1 FROM unnest(p."Places") x WHERE lower(x) = ANY(me.places)))
                  AND ((me.state IS NOT NULL AND p."HomeStateName" = me.state)
                       OR (me.district IS NOT NULL AND p."LastDistrictId" = me.district AND p."LastDistrictAt" > now() - {Stale}))
                LIMIT {TagCandidatesMax}) c, me
          ORDER BY md5(@me::text || c.id::text)
          LIMIT 300),
        src AS (
          SELECT tc.id, {(int)DiscoverFlags.SameTrip} AS flag FROM tc
          UNION ALL SELECT mate.id, {(int)DiscoverFlags.TravelledWith} FROM mate
          UNION ALL SELECT fol.id, {(int)DiscoverFlags.FollowsYou} FROM fol
          UNION ALL SELECT mut.id, {(int)DiscoverFlags.Mutual} FROM mut
          UNION ALL SELECT near.id, {(int)DiscoverFlags.Near} FROM near
          UNION ALL SELECT intr.id, (CASE WHEN cardinality(intr.iidx) > 0 THEN {(int)DiscoverFlags.Interests} ELSE 0 END)
                                  | (CASE WHEN cardinality(intr.pidx) > 0 THEN {(int)DiscoverFlags.Places} ELSE 0 END) FROM intr),
        agg AS (SELECT s.id, bit_or(s.flag)::int AS flags FROM src s WHERE s.flag <> 0 GROUP BY s.id)
        SELECT a.id AS "UserId", a.flags AS "Flags", COALESCE(mu.cnt, 0) AS "MutualCount", mu.first AS "MutualFirstId",
               COALESCE(tc.city, mt.city) AS "CityKey", tc.month AS "Month", ir.iidx AS "InterestIdx", ir.pidx AS "PlaceIdx",
               p."IsPrivate" AS "IsPrivate"
        FROM agg a
        JOIN "Users" u ON u."Id" = a.id AND {ActiveSeeker("u")}
        JOIN "UserProfiles" p ON p."UserId" = a.id
        LEFT JOIN tc ON tc.id = a.id
        LEFT JOIN mate mt ON mt.id = a.id
        LEFT JOIN mut mu ON mu.id = a.id
        LEFT JOIN intr ir ON ir.id = a.id
        WHERE a.id <> @me AND {Excluded("a.id")}
        """;

    // Page hydrate (DESIGN 3.3): every exclusion again, live person columns, live mutuals (middle person active, not blocked with
    // me, his follow accepted) and the tags I share with him now.
    internal static readonly string HydrateSql = $"""
        WITH ids AS (SELECT x.id, x.n FROM unnest(@ids) WITH ORDINALITY AS x(id, n)),
        mine AS (SELECT p."Interests" AS ints, ARRAY(SELECT lower(x) FROM unnest(p."Places") x) AS places
                 FROM "UserProfiles" p WHERE p."UserId" = @me),
        m AS (
          SELECT b."FolloweeId" AS id, count(*)::int AS cnt,
                 (array_agg(COALESCE(mu."Name", '') ORDER BY a."CreatedAt" DESC))[1] AS first
          FROM "Follows" a
          JOIN "Follows" b ON b."FollowerId" = a."FolloweeId" AND b."Status" = '{FollowStatuses.Accepted}' AND b."FolloweeId" IN (SELECT id FROM ids)
          JOIN "Users" mu ON mu."Id" = a."FolloweeId" AND {ActiveSeeker("mu")}
          WHERE a."FollowerId" = @me AND a."Status" = '{FollowStatuses.Accepted}' AND NOT {BlockedEitherWay("@me", "a.\"FolloweeId\"")}
          GROUP BY b."FolloweeId")
        SELECT u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username", u."ProfilePhotoUrl" AS "PhotoUrl",
               u."CompletedTripsCount", p."IsPrivate" AS "IsPrivate",
               {AcceptedFollow("u.\"Id\"", "@me")} AS "FollowsMe",
               COALESCE(m.cnt, 0) AS "MutualCount", m.first AS "MutualName",
               ARRAY(SELECT o.v FROM unnest(p."Interests") WITH ORDINALITY AS o(v, n) WHERE o.v = ANY(mine.ints) ORDER BY o.n) AS "SharedInterests",
               ARRAY(SELECT o.v FROM unnest(p."Places") WITH ORDINALITY AS o(v, n) WHERE lower(o.v) = ANY(mine.places) ORDER BY o.n) AS "SharedPlaces"
        FROM ids
        JOIN "Users" u ON u."Id" = ids.id AND {ActiveSeeker("u")}
        JOIN "UserProfiles" p ON p."UserId" = u."Id"
        LEFT JOIN mine ON TRUE
        LEFT JOIN m ON m.id = u."Id"
        WHERE u."Id" <> @me AND {Excluded("u.\"Id\"")}
        ORDER BY ids.n
        """;

    // DESIGN 1.3 Similar travellers: only when the viewed person is visible to me (public, or I follow him). Post-city and
    // interest candidates are public only; a private candidate can only come from his following list.
    internal static readonly string SimilarSql = $"""
        WITH him AS (
          SELECT p."UserId", p."Interests", p."HomeStateName", COALESCE(hu."Name", '') AS name,
                 CASE WHEN p."LastDistrictAt" > now() - {Stale} THEN p."LastDistrictId" END AS district
          FROM "UserProfiles" p JOIN "Users" hu ON hu."Id" = p."UserId" AND {ActiveSeeker("hu")}
          WHERE p."UserId" = @him AND @him <> @me AND NOT {BlockedEitherWay("@me", "@him")}
            AND (NOT p."IsPrivate" OR {AcceptedFollow("@me", "@him")})),
        fol AS (
          SELECT f."FolloweeId" AS id FROM "Follows" f
          WHERE f."FollowerId" = @him AND f."Status" = '{FollowStatuses.Accepted}' AND EXISTS (SELECT 1 FROM him)
          ORDER BY f."CreatedAt" DESC LIMIT 200),
        cities AS (
          SELECT c."CityKey", c."CityName" FROM (
            SELECT DISTINCT ON (t."CityKey") t."CityKey", t."CityName", t."PublishedAt" FROM "Trips" t
            WHERE t."OwnerUserId" = @him AND EXISTS (SELECT 1 FROM him) AND {PublicTripReader.LivePost("t")}
            ORDER BY t."CityKey", t."PublishedAt" DESC) c
          ORDER BY c."PublishedAt" DESC LIMIT 6),
        own AS (
          SELECT y.id, y.city FROM (
            SELECT DISTINCT ON (x.id) x.id, x.city, x.at
            FROM cities c
            CROSS JOIN LATERAL (SELECT t."OwnerUserId" AS id, c."CityName" AS city, t."PublishedAt" AS at
                                FROM "Trips" t
                                JOIN "UserProfiles" op ON op."UserId" = t."OwnerUserId" AND NOT op."IsPrivate"
                                WHERE t."CityKey" = c."CityKey" AND {PublicTripReader.LivePost("t")}
                                  AND t."OwnerUserId" <> @me AND t."OwnerUserId" <> @him
                                ORDER BY t."PublishedAt" DESC LIMIT {SimilarPostsPerCity}) x
            ORDER BY x.id, x.at DESC) y
          ORDER BY y.at DESC LIMIT 100),
        intr AS (
          SELECT c.id,
                 ARRAY(SELECT o.v FROM unnest(c.ints) WITH ORDINALITY AS o(v, n) WHERE o.v = ANY(him."Interests") ORDER BY o.n) AS tags
          FROM (SELECT p."UserId" AS id, p."Interests" AS ints
                FROM "UserProfiles" p, him
                WHERE p."UserId" <> @me AND p."UserId" <> @him AND NOT p."IsPrivate" AND p."Interests" && him."Interests"
                  AND ((him."HomeStateName" IS NOT NULL AND p."HomeStateName" = him."HomeStateName")
                       OR (him.district IS NOT NULL AND p."LastDistrictId" = him.district AND p."LastDistrictAt" > now() - {Stale}))
                LIMIT {TagCandidatesMax}) c, him
          ORDER BY md5(@me::text || c.id::text)
          LIMIT 100),
        src AS (
          SELECT fol.id, {(int)DiscoverFlags.Mutual} AS flag FROM fol
          UNION ALL SELECT own.id, {(int)DiscoverFlags.SameTrip} FROM own
          UNION ALL SELECT intr.id, {(int)DiscoverFlags.Interests} FROM intr),
        agg AS (SELECT s.id, bit_or(s.flag)::int AS flags FROM src s GROUP BY s.id)
        SELECT u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username", u."ProfilePhotoUrl" AS "PhotoUrl",
               u."CompletedTripsCount", p."IsPrivate" AS "IsPrivate", {AcceptedFollow("u.\"Id\"", "@me")} AS "FollowsMe",
               a.flags AS "Flags", o.city AS "CityName", i.tags AS "Tags", (SELECT him.name FROM him) AS "ViaName"
        FROM agg a
        JOIN "Users" u ON u."Id" = a.id AND {ActiveSeeker("u")}
        JOIN "UserProfiles" p ON p."UserId" = a.id
        LEFT JOIN own o ON o.id = a.id
        LEFT JOIN intr i ON i.id = a.id
        WHERE a.id <> @me AND a.id <> @him AND {Excluded("a.id")}
        """;

    // DESIGN 1.2: idempotent, and an unknown or own id is a silent no-op (no 500, so no oracle).
    internal const string HideSql = """
        INSERT INTO "DiscoverHides" ("UserId", "HiddenUserId", "HiddenAt")
        SELECT @me, @him, now()
        WHERE @him <> @me AND EXISTS (SELECT 1 FROM "Users" WHERE "Id" = @him)
        ON CONFLICT ("UserId", "HiddenUserId") DO UPDATE SET "HiddenAt" = now()
        """;

    // ------------------------------------------------------------------ page

    public async Task<DiscoverPageDto> PageAsync(Guid me, string chip, DiscoverCursorValue? cursor, int limit, bool refresh, CancellationToken ct = default)
    {
        if (refresh && cursor is null) await cache.RefreshAsync(me);
        var withTrip = chip == DiscoverChips.SameTrip && cursor is null;
        var start = cursor?.ZScore;
        var view = await ViewAsync(me, start, withTrip, ct);
        var scan = await DiscoverPaging.ScanAsync(view.Read, start, chip);
        var survivors = await HydrateAsync(me, chip, scan.Matched, ct);
        var (take, cursorRow) = DiscoverPaging.Cut(survivors.Select(s => s.Row).ToList(), scan, limit);
        return new DiscoverPageDto
        {
            Items = survivors.Take(take).Select(s => s.Dto).ToList(),
            NextCursor = DiscoverPaging.Cursor(view.Stamp, cursorRow),
            MyTrip = withTrip ? await TripHeaderAsync(view.Trip, ct) : null,
        };
    }

    private sealed record View(long Stamp, string? Trip, Func<double?, Task<IReadOnlyList<DiscoverSnapshotRow>>> Read);

    // The cached snapshot when there is one; else one build (stampede lock: the winner writes, a loser waits up to 1 s for it and
    // then builds for himself without writing). With no Redis every call builds.
    private async Task<View> ViewAsync(Guid me, double? start, bool withTrip, CancellationToken ct)
    {
        if (await cache.ReadAsync(me, start, DiscoverPaging.ReadSize, withTrip) is { } first) return Cached(me, first, start);
        var token = await cache.TryLockAsync(me);
        if (token is null)
        {
            if (cache.Available && await cache.WaitForSnapshotAsync(me, ct)
                && await cache.ReadAsync(me, start, DiscoverPaging.ReadSize, withTrip) is { } waited)
                return Cached(me, waited, start);
            return Memory(await BuildAsync(me, ct));
        }
        try
        {
            var built = await BuildAsync(me, ct);
            await cache.WriteAsync(me, built.Stamp, built.Rows, built.Trip);
            return Memory(built);
        }
        finally
        {
            await cache.ReleaseAsync(me, token);
        }
    }

    private View Cached(Guid me, DiscoverSlice first, double? start) => new(first.Stamp, first.Trip, async bound =>
    {
        if (bound == start) return first.Rows;
        return (await cache.ReadAsync(me, bound, DiscoverPaging.ReadSize, false))?.Rows ?? [];
    });

    private static View Memory(Built built) => new(built.Stamp, built.Trip, bound =>
        Task.FromResult<IReadOnlyList<DiscoverSnapshotRow>>(
            built.Rows.Where(r => bound is null || r.Score < bound.Value).Take(DiscoverPaging.ReadSize).ToList()));

    private async Task<DiscoverTripDto?> TripHeaderAsync(string? trip, CancellationToken ct)
    {
        if (trip is null) return null;
        var p = trip.Split('|');
        if (p.Length != 2 || !long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var key)
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month) || month <= 0) return null;
        return await CityNameAsync(key, ct) is { } name ? new DiscoverTripDto { CityName = name, Month = month } : null;
    }

    // ------------------------------------------------------------------ build

    public sealed record Built(long Stamp, IReadOnlyList<DiscoverSnapshotRow> Rows, string? Trip);

    private async Task<Built> BuildAsync(Guid me, CancellationToken ct)
    {
        var now = Now;
        var today = IstTime.TodayIst(now);
        var trips = await db.Database.SqlQueryRaw<MyTripRow>(MyTripsSql, Uuid("me", me), Date("today", today)).ToListAsync(ct);
        var tripCands = new List<(Guid Id, long City, int Month)>();
        if (trips.Count > 0)
        {
            var posts = await db.Database.SqlQueryRaw<PostRow>(PostsSql, Uuid("me", me), Date("today", today), Int("take", PostsScanMax),
                new NpgsqlParameter("cities", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = trips.Select(t => t.CityKey).Distinct().ToArray() })
                .ToListAsync(ct);
            tripCands = TripCandidates(trips, posts);
        }
        var rows = await db.Database.SqlQueryRaw<CandidateRow>(SnapshotSql, Uuid("me", me),
            Uuids("tripIds", tripCands.Select(c => c.Id).ToArray()),
            new NpgsqlParameter("tripCity", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = tripCands.Select(c => c.City).ToArray() },
            new NpgsqlParameter("tripMonth", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = tripCands.Select(c => c.Month).ToArray() })
            .ToListAsync(ct);
        var built = Assemble(rows, trips.Count > 0 ? trips[0] : null, now);
        logger.LogInformation("discover.snapshot.built {UserId} {Rows}", me, built.Rows.Count);
        return built;
    }

    // The sameTrip source (DESIGN 2.1 trip_c): newest post first, one row per owner, kept only when a trip of mine in the same
    // city has near dates; at most 300.
    internal static List<(Guid Id, long City, int Month)> TripCandidates(IReadOnlyList<MyTripRow> trips, IEnumerable<PostRow> posts)
    {
        var spans = trips.Select(t => (t.CityKey, Span: MySpan(t))).ToList();
        var seen = new HashSet<Guid>();
        var result = new List<(Guid, long, int)>();
        foreach (var p in posts)
        {
            if (seen.Contains(p.OwnerUserId)) continue;
            var span = DiscoverRules.PostSpan(p.StartDate, p.PostDays, p.PostStartMonth, p.PublishedAt, p.PostWindowMonths);
            if (!spans.Any(m => m.CityKey == p.CityKey && DiscoverRules.DatesNear(m.Span, span))) continue;
            seen.Add(p.OwnerUserId);
            result.Add((p.OwnerUserId, p.CityKey, p.StartDate is { } s ? SocialMonth.Key(s) : Math.Max(p.PostStartMonth ?? 0, 0)));
            if (result.Count >= TripCandidatesMax) break;
        }
        return result;
    }

    internal static DiscoverSpan MySpan(MyTripRow t)
        => t.StartDate is { } s ? DiscoverSpan.Exact(s, t.EndDate ?? s) : DiscoverRules.PostSpan(null, null, t.PostStartMonth, t.PublishedAt, t.PostWindowMonths);

    // Scores, the per-chip quota and the cached row codes. Private rows keep only their allowed sources.
    internal static Built Assemble(IReadOnlyList<CandidateRow> rows, MyTripRow? soonest, DateTime nowUtc)
    {
        var codes = new Dictionary<Guid, DiscoverRowCode>();
        var candidates = new List<DiscoverCandidate>();
        foreach (var r in rows)
        {
            var flags = DiscoverRules.VisibleFlags((DiscoverFlags)r.Flags, r.IsPrivate);
            if (flags == DiscoverFlags.None || codes.ContainsKey(r.UserId)) continue;
            var iidx = flags.HasFlag(DiscoverFlags.Interests) ? (r.InterestIdx ?? []) : [];
            var pidx = flags.HasFlag(DiscoverFlags.Places) ? (r.PlaceIdx ?? []) : [];
            var mutual = flags.HasFlag(DiscoverFlags.Mutual) ? r.MutualCount : 0;
            var points = DiscoverRules.Points(flags, mutual, iidx.Length, pidx.Length);
            var hasCity = flags.HasFlag(DiscoverFlags.SameTrip) || flags.HasFlag(DiscoverFlags.TravelledWith);
            codes[r.UserId] = new DiscoverRowCode(flags, points, mutual, flags.HasFlag(DiscoverFlags.Mutual) ? r.MutualFirstId : null,
                hasCity ? r.CityKey : null, flags.HasFlag(DiscoverFlags.SameTrip) && r.Month is > 0 ? r.Month : null, iidx, pidx);
            candidates.Add(new DiscoverCandidate(r.UserId, flags, points));
        }
        var selected = DiscoverRules.SelectSnapshot(candidates)
            .Select(c => new DiscoverSnapshotRow(c.UserId, DiscoverRules.ZScore(c.Points, c.UserId), codes[c.UserId]))
            .ToList();
        return new Built(new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), selected, TripField(selected, soonest));
    }

    // The "ALSO GOING TO MANALI · DEC" header: only when every sameTrip row is in the city of my soonest trip and its month is known.
    internal static string? TripField(IReadOnlyList<DiscoverSnapshotRow> rows, MyTripRow? soonest)
    {
        if (soonest is null) return null;
        var same = rows.Where(r => r.Code is { } c && c.Flags.HasFlag(DiscoverFlags.SameTrip)).Select(r => r.Code!.CityKey).ToList();
        if (same.Count == 0 || same.Any(k => k != soonest.CityKey)) return null;
        var span = MySpan(soonest);
        var month = soonest.StartDate is { } s ? SocialMonth.Key(s) : span.HasMonths ? span.FirstMonth : 0;
        return month > 0 ? string.Create(CultureInfo.InvariantCulture, $"{soonest.CityKey}|{month}") : null;
    }

    // ------------------------------------------------------------------ hydrate

    private async Task<List<(DiscoverSnapshotRow Row, DiscoverPersonDto Dto)>> HydrateAsync(
        Guid me, string chip, IReadOnlyList<DiscoverSnapshotRow> matched, CancellationToken ct)
    {
        if (matched.Count == 0) return [];
        var live = (await db.Database.SqlQueryRaw<LiveRow>(HydrateSql, Uuid("me", me), Uuids("ids", matched.Select(m => m.UserId).ToArray()))
            .ToListAsync(ct)).ToDictionary(r => r.UserId);
        var picked = new List<(DiscoverSnapshotRow Row, DiscoverRowCode Code, LiveRow Live, DiscoverFlags Flags, string Kind)>();
        foreach (var row in matched)
        {
            if (row.Code is not { } code || !live.TryGetValue(row.UserId, out var l)) continue;
            var flags = LiveFlags(code.Flags, l);
            if (flags == DiscoverFlags.None || !DiscoverRules.Matches(flags, chip)) continue;
            var kind = DiscoverRules.ReasonFor(chip, flags, l.MutualCount, (l.SharedInterests ?? []).Length, (l.SharedPlaces ?? []).Length);
            if (kind is null) continue;
            picked.Add((row, code, l, flags, kind));
        }
        var cityKeys = picked.Where(p => !p.Live.IsPrivate && p.Kind is DiscoverReasonKinds.SameTrip or DiscoverReasonKinds.TravelledWith)
            .Select(p => p.Code.CityKey).OfType<long>().Distinct().ToList();
        var names = new Dictionary<long, string?>();
        foreach (var key in cityKeys) names[key] = await CityNameAsync(key, ct); // batch-loop: cached catalog lookups, a few per page
        return picked.Select(p => (p.Row, new DiscoverPersonDto
        {
            UserId = p.Live.UserId, Name = p.Live.Name, Username = p.Live.Username, Initial = SocialRules.Initial(p.Live.Name),
            PhotoUrl = Face(p.Live.PhotoUrl), Level = TravelLevels.Level(p.Live.CompletedTripsCount),
            FollowsMe = p.Live.FollowsMe, IFollow = false, FollowRequested = false, IsPrivate = p.Live.IsPrivate,
            Reason = Reason(p.Kind, p.Flags, p.Code, p.Live.IsPrivate, p.Code.CityKey is { } k ? names.GetValueOrDefault(k) : null,
                p.Live.MutualCount, p.Live.MutualName, p.Live.SharedInterests ?? [], p.Live.SharedPlaces ?? []),
        })).ToList();
    }

    // The cached flags corrected by the live read: a lost mutual, follow-back or shared tag drops that source; a person who turned
    // private keeps only travelledWith / followsYou / mutual (DESIGN 2.3).
    internal static DiscoverFlags LiveFlags(DiscoverFlags cached, LiveRow l)
    {
        var flags = cached;
        if (l.MutualCount <= 0) flags &= ~DiscoverFlags.Mutual;
        if (!l.FollowsMe) flags &= ~DiscoverFlags.FollowsYou;
        if ((l.SharedInterests ?? []).Length == 0) flags &= ~DiscoverFlags.Interests;
        if ((l.SharedPlaces ?? []).Length == 0) flags &= ~DiscoverFlags.Places;
        return DiscoverRules.VisibleFlags(flags, l.IsPrivate);
    }

    // Only the fields of the kind; a private row never carries tags, a city or a month; near never carries a place.
    internal static DiscoverReasonDto Reason(string kind, DiscoverFlags flags, DiscoverRowCode code, bool isPrivate, string? cityName,
        int mutualCount, string? mutualName, IReadOnlyList<string> interests, IReadOnlyList<string> places)
    {
        var reason = new DiscoverReasonDto { Kind = kind };
        switch (kind)
        {
            case DiscoverReasonKinds.SameTrip when !isPrivate:
                reason.CityName = cityName;
                reason.Month = code.Month;
                break;
            case DiscoverReasonKinds.TravelledWith when !isPrivate:
                reason.CityName = cityName;
                break;
            case DiscoverReasonKinds.Mutual:
                reason.MutualName = string.IsNullOrWhiteSpace(mutualName) ? null : SocialRules.FirstName(mutualName);
                reason.MutualCount = Math.Min(Math.Max(mutualCount, 0), SocialLimits.MutualsCountCap);
                break;
            case DiscoverReasonKinds.Near when !isPrivate && flags.HasFlag(DiscoverFlags.Interests) && interests.Count > 0:
                reason.Tags = interests.Take(TagsShown).ToList();
                break;
            case DiscoverReasonKinds.Interests when !isPrivate:
                reason.Tags = interests.Take(TagsShown).ToList();
                break;
            case DiscoverReasonKinds.Places when !isPrivate:
                reason.Tags = places.Take(TagsShown).ToList();
                break;
        }
        return reason;
    }

    private async Task<string?> CityNameAsync(long key, CancellationToken ct)
    {
        try
        {
            var (available, city) = await catalog.FindAsync(key, ct);
            return available ? city?.Name : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "discover.city_name_failed {CityKey}", key);
            return null;
        }
    }

    // ------------------------------------------------------------------ hide, similar

    public Task HideAsync(Guid me, Guid him, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(HideSql, [Uuid("me", me), Uuid("him", him)], ct);

    public async Task<DiscoverSimilarDto> SimilarAsync(Guid me, Guid him, int limit, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<SimilarRow>(SimilarSql, Uuid("me", me), Uuid("him", him), Date("today", IstTime.TodayIst(Now)))
            .ToListAsync(ct);
        return new DiscoverSimilarDto { Items = RankSimilar(rows, limit) };
    }

    internal static List<DiscoverPersonDto> RankSimilar(IEnumerable<SimilarRow> rows, int limit)
    {
        var list = new List<(double Z, DiscoverPersonDto Dto)>();
        foreach (var r in rows)
        {
            var tags = r.Tags ?? [];
            var flags = DiscoverRules.VisibleFlags((DiscoverFlags)r.Flags, r.IsPrivate);
            if (tags.Length == 0) flags &= ~DiscoverFlags.Interests;
            if (string.IsNullOrWhiteSpace(r.CityName)) flags &= ~DiscoverFlags.SameTrip;
            if (flags == DiscoverFlags.None) continue;
            var kind = DiscoverRules.TopReason(flags, 1, tags.Length, 0)!;
            var reason = new DiscoverReasonDto { Kind = kind };
            if (kind == DiscoverReasonKinds.SameTrip) reason.CityName = r.CityName;
            else if (kind == DiscoverReasonKinds.Interests) reason.Tags = tags.Take(TagsShown).ToList();
            else if (kind == DiscoverReasonKinds.Mutual)
            {
                var via = SocialRules.FirstName(r.ViaName);
                reason.MutualName = string.IsNullOrWhiteSpace(via) ? null : via;
            }
            list.Add((DiscoverRules.ZScore(DiscoverRules.Points(flags, 1, tags.Length, 0), r.UserId), new DiscoverPersonDto
            {
                UserId = r.UserId, Name = r.Name, Username = r.Username, Initial = SocialRules.Initial(r.Name), PhotoUrl = Face(r.PhotoUrl),
                Level = TravelLevels.Level(r.CompletedTripsCount), FollowsMe = r.FollowsMe, IsPrivate = r.IsPrivate, Reason = reason,
            }));
        }
        return list.OrderByDescending(x => x.Z).Take(limit).Select(x => x.Dto).ToList();
    }

    // ------------------------------------------------------------------ rows

    public sealed class MyTripRow
    {
        public long CityKey { get; set; }
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public int? PostStartMonth { get; set; }
        public DateTime? PublishedAt { get; set; }
        public short? PostWindowMonths { get; set; }
    }

    public sealed class PostRow
    {
        public Guid OwnerUserId { get; set; }
        public long CityKey { get; set; }
        public DateOnly? StartDate { get; set; }
        public short? PostDays { get; set; }
        public int? PostStartMonth { get; set; }
        public DateTime? PublishedAt { get; set; }
        public short? PostWindowMonths { get; set; }
    }

    public sealed class CandidateRow
    {
        public Guid UserId { get; set; }
        public int Flags { get; set; }
        public int MutualCount { get; set; }
        public Guid? MutualFirstId { get; set; }
        public long? CityKey { get; set; }
        public int? Month { get; set; }
        public int[]? InterestIdx { get; set; }
        public int[]? PlaceIdx { get; set; }
        public bool IsPrivate { get; set; }
    }

    public sealed class LiveRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
        public int CompletedTripsCount { get; set; }
        public bool IsPrivate { get; set; }
        public bool FollowsMe { get; set; }
        public int MutualCount { get; set; }
        public string? MutualName { get; set; }
        public string[]? SharedInterests { get; set; }
        public string[]? SharedPlaces { get; set; }
    }

    public sealed class SimilarRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
        public int CompletedTripsCount { get; set; }
        public bool IsPrivate { get; set; }
        public bool FollowsMe { get; set; }
        public int Flags { get; set; }
        public string? CityName { get; set; }
        public string[]? Tags { get; set; }
        public string? ViaName { get; set; }
    }
}
