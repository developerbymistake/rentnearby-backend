using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Owner orders 2026-11-27 (post_notify/SPEC.md 3, 5, use cases 2, 4-14, 24): ONE morning push at 07:00 IST that merges every
// new nearby trip for a traveller. One set-based statement (no per-user query) picks the posts whose poster asked
// "Notify travellers near me" and published before today's 07:00 IST, re-checks them at send time (still public and live,
// public poster, starts within 60 days, seats left, fewer than 50 waiting requests), finds travellers whose last district is
// within 150 km (district centroid; the trip's own district always counts), applies the switches, the gender rule, blocks,
// followers (they have their own push), members and requesters, keeps the 200 nearest per post, writes one bell row per
// (traveller, post) and marks the posts done. Then one push per traveller (key nearby:{user}:{yyyyMMdd}) in the same
// transaction: a second run, a crash or a restart never sends twice. Outside 07:00-21:00 IST nothing is sent (catch-up).
// A post still pending 7 days after publish (full, paused, poster private, ended, unpublished) is closed without a send:
// it is no longer "new" and it never stays in the pending index for ever. Travellers of one district share its centroid
// distance, so ties are broken by a per-post hash (not by user id: the same 200 low ids would otherwise get every post).
// A Men only / Women only post is sent only while the poster is of that gender (PostNotifyRules.PosterFitsAudience).
public sealed class NearbyDigestJob(ApplicationDbContext db, INoticeWriter notices, TimeProvider clock, ILogger<NearbyDigestJob> logger)
    : IBuddyFeatureJob
{
    public string Name => "social-nearby-digest";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public TimeOnly? DailyAtIst => PostNotifyRules.DigestAtIst;

    // The moment of the publish that queued the post: the latest publish (LastPublishedAt), not PublishedAt, which bump protection
    // keeps for 7 days on a re-publish. With PublishedAt a re-publish late in that week was closed as stale at the next run and
    // nobody nearby was told although the poster's notified-post slot was used (verifier 2026-11-27, use cases 2 and 27).
    internal static string PublishMoment(string t) => $"COALESCE({t}.\"LastPublishedAt\", {t}.\"PublishedAt\")";

    internal static readonly string DigestSql = $"""
        WITH posts AS MATERIALIZED (
          SELECT t."Id", t."OwnerUserId", t."JoinGender", t."DistrictId",
                 ST_SetSRID(ST_MakePoint(t."CityLongitude", t."CityLatitude"), 4326)::geography AS g
          FROM "Trips" t
          JOIN "Users" o ON o."Id" = t."OwnerUserId" AND {ActiveSeeker("o")}
          JOIN "UserProfiles" op ON op."UserId" = t."OwnerUserId" AND NOT op."IsPrivate"
            AND (t."JoinGender" = '{JoinGenders.Anyone}' OR (t."JoinGender" = '{JoinGenders.Men}' AND op."Gender" = '{SocialGenders.Male}')
                 OR (t."JoinGender" = '{JoinGenders.Women}' AND op."Gender" = '{SocialGenders.Female}'))
          WHERE t."NotifyNearby" AND t."NearbyNotifiedAt" IS NULL AND {PublishMoment("t")} < @cutoff AND {PublishMoment("t")} >= @stale
            AND {PublicTripReader.LivePost("t")}
            AND (t."StartDate" IS NULL OR t."StartDate" <= @soon)
            AND (SELECT count(*) FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."Status" = '{TripMemberStatuses.Joined}')
                < LEAST(COALESCE(t."Seats", 0), {BuddyLimits.MaxViewers})
            AND (SELECT count(*) FROM "TripJoinRequests" r WHERE r."TripId" = t."Id" AND r."Status" = '{JoinRequestStatuses.Waiting}')
                < {PostNotifyRules.PendingRequestsMax}
          ORDER BY {PublishMoment("t")}
          LIMIT {PostNotifyRules.NearbyPostsPerRun}),
        areas AS MATERIALIZED (
          SELECT d."Id", ST_Centroid(d."Boundary")::geography AS c
          FROM "Districts" d
          WHERE d."Boundary" IS NOT NULL
            AND d."Id" IN (SELECT pr."LastDistrictId" FROM "UserProfiles" pr
                           WHERE pr."NotifyNearbyTrips" AND pr."LastDistrictId" IS NOT NULL AND pr."LastDistrictAt" >= @fresh)),
        near AS (
          SELECT p."Id" AS trip_id, a."Id" AS district_id, ST_Distance(a.c, p.g) AS dist
          FROM posts p JOIN areas a ON a."Id" = p."DistrictId" OR ST_DWithin(a.c, p.g, {PostNotifyRules.NearbyKm * 1000})),
        cand AS (
          SELECT n.trip_id, p."OwnerUserId" AS poster, pr."UserId" AS user_id,
                 row_number() OVER (PARTITION BY n.trip_id ORDER BY n.dist, md5(pr."UserId"::text || n.trip_id::text)) AS rn
          FROM near n
          JOIN posts p ON p."Id" = n.trip_id
          JOIN "UserProfiles" pr ON pr."LastDistrictId" = n.district_id AND pr."NotifyNearbyTrips" AND pr."LastDistrictAt" >= @fresh
          JOIN "Users" u ON u."Id" = pr."UserId" AND {ActiveSeeker("u")}
          WHERE pr."UserId" <> p."OwnerUserId"
            AND (p."JoinGender" = '{JoinGenders.Anyone}' OR (p."JoinGender" = '{JoinGenders.Men}' AND pr."Gender" = '{SocialGenders.Male}')
                 OR (p."JoinGender" = '{JoinGenders.Women}' AND pr."Gender" = '{SocialGenders.Female}'))
            AND NOT {BlockedEitherWay("pr.\"UserId\"", "p.\"OwnerUserId\"")}
            AND NOT {AcceptedFollow("pr.\"UserId\"", "p.\"OwnerUserId\"")}
            AND NOT EXISTS (SELECT 1 FROM "UserNotices" x WHERE x."UserId" = pr."UserId" AND x."TripId" = n.trip_id
                            AND x."Kind" IN ('{NoticeKinds.FollowedPosted}', '{NoticeKinds.NearbyPosted}'))
            AND NOT EXISTS (SELECT 1 FROM "TripMembers" m WHERE m."TripId" = n.trip_id AND m."UserId" = pr."UserId")
            AND NOT EXISTS (SELECT 1 FROM "TripJoinRequests" r WHERE r."TripId" = n.trip_id AND r."RequesterId" = pr."UserId")),
        ins AS (
          INSERT INTO "UserNotices" ("Id", "UserId", "Kind", "TripId", "ActorUserId", "RefId", "Amount", "Count", "CreatedAt", "UpdatedAt")
          SELECT gen_random_uuid(), c.user_id, '{NoticeKinds.NearbyPosted}', c.trip_id, c.poster, NULL, NULL, 1, now(), now()
          FROM cand c WHERE c.rn <= {PostNotifyRules.NearbyPerPostMax}
          ON CONFLICT ("UserId", "Kind", "TripId") DO NOTHING
          RETURNING "UserId", "TripId", "ActorUserId"),
        fol AS (
          SELECT x."UserId", x."TripId", x."ActorUserId" FROM "UserNotices" x
          WHERE @followersInDigest AND x."Kind" = '{NoticeKinds.FollowedPosted}' AND x."ReadAt" IS NULL
            AND x."CreatedAt" >= @prevCutoff AND x."CreatedAt" < @cutoff),
        done AS (
          UPDATE "Trips" t SET "NearbyNotifiedAt" = now() FROM posts p WHERE t."Id" = p."Id" RETURNING t."Id"),
        expired AS (
          UPDATE "Trips" t SET "NearbyNotifiedAt" = now()
          WHERE t."NotifyNearby" AND t."NearbyNotifiedAt" IS NULL AND {PublishMoment("t")} < @stale RETURNING t."Id"),
        told AS (SELECT i."UserId", i."TripId", i."ActorUserId" FROM ins i UNION SELECT f."UserId", f."TripId", f."ActorUserId" FROM fol f)
        SELECT k."UserId", count(*)::int AS "Trips",
               (array_agg(k."TripId" ORDER BY k."TripId"))[1] AS "TripId",
               COALESCE((array_agg(a."Name" ORDER BY k."TripId"))[1], '') AS "PosterName"
        FROM told k LEFT JOIN "Users" a ON a."Id" = k."ActorUserId"
        GROUP BY k."UserId"
        """;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!PostNotifyRules.DigestMaySend(now)) return 0;
        var today = IstTime.TodayIst(now);
        var cutoff = PostNotifyRules.DigestCutoffUtc(today);
        // One set-based statement over every pending post: give it more than the 30 s default so a big morning never fails and
        // rolls over to the next day (a failed run marks the day done in the job runner).
        db.Database.SetCommandTimeout(PostNotifyRules.DigestCommandTimeout);
        var told = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var rows = await db.Database.SqlQueryRaw<ToldRow>(DigestSql,
                At("cutoff", cutoff), At("prevCutoff", cutoff.AddDays(-1)), At("fresh", now - PostNotifyRules.AreaStaleAfter),
                At("stale", cutoff - PostNotifyRules.NearbyPendingMaxAge),
                Date("today", today), Date("soon", today.AddDays(PostNotifyRules.NearbyStartWithinDays)),
                new NpgsqlParameter("followersInDigest", NpgsqlDbType.Boolean) { Value = !PostNotifyRules.FollowersImmediate }).ToListAsync(c);
            foreach (var chunk in rows.Chunk(PostNotifyRules.PushBatch)) // batch-loop: one outbox statement per 1,000 travellers
                await notices.PushAsync(chunk.Select(r => SocialPushCopy.NearbyDigest(r.UserId, r.Trips, r.TripId, r.PosterName, today)).ToList(), c);
            await tx.CommitAsync(c);
            return rows.Count;
        }, ct, logger);
        if (told > 0) notices.Wake();
        logger.LogInformation("social.nearby.digest {Users}", told);
        return told;
    }

    public sealed class ToldRow
    {
        public Guid UserId { get; set; }
        public int Trips { get; set; }
        public Guid TripId { get; set; }
        public string PosterName { get; set; } = string.Empty;
    }
}
