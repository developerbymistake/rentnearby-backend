using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Owner orders 2026-11-27 (post_notify/SPEC.md): what a publish tells, inside the publish transaction and under the poster's
// trip-slots lock (every publish path holds it, so the rolling caps on the profile row are exact).
// - followers: bell row + immediate push (PostNotifyRules.FollowersImmediate), max 2,000, gender rule, switch, blocks;
// - nearby: the toggle is only recorded here (Trips.NotifyNearby); NearbyDigestJob tells at 07:00 IST;
// - caps: 4 publishes / 7 days, never refunded; a post notifies only once (NotifiedAt). No weekly notified-post cap (owner 2026-11-28).
public static class PostNotifier
{
    internal const string PosterSql = """
        SELECT COALESCE(u."Name", '') AS "Name", p."IsPrivate", p."Gender", p."PublishTimes"
        FROM "Users" u JOIN "UserProfiles" p ON p."UserId" = u."Id"
        WHERE u."Id" = @me
        """;

    public sealed class Poster
    {
        public string Name { get; set; } = string.Empty;
        public bool IsPrivate { get; set; }
        public string? Gender { get; set; }
        public DateTime[] PublishTimes { get; set; } = [];
    }

    public static async Task<Poster?> ReadAsync(ApplicationDbContext db, Guid me, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<Poster>(PosterSql, Uuid("me", me)).ToListAsync(ct)).FirstOrDefault();

    // The 429 data of PublishLimit ({limit, used, nextAllowedAt}), or null when one more publish is allowed.
    public static object? PublishRefusal(Poster poster, DateTime nowUtc)
    {
        var inWindow = PostNotifyRules.InWindow(poster.PublishTimes, nowUtc);
        if (inWindow.Length < PostNotifyRules.PublishesPerWeek) return null;
        return new
        {
            limit = PostNotifyRules.PublishesPerWeek, used = inWindow.Length,
            nextAllowedAt = PostNotifyRules.NextAllowedAt(inWindow, PostNotifyRules.PublishesPerWeek),
        };
    }

    // Followers who want trip news: bell row now (idempotent per follower and trip); RETURNING gives only the rows written, so a
    // replay or a second publish never tells the same follower twice.
    internal static readonly string FollowersSql = $"""
        WITH x AS (
          SELECT f."FollowerId" FROM "Follows" f
          JOIN "UserProfiles" p ON p."UserId" = f."FollowerId" AND p."NotifyFollowedTrips"
          JOIN "Users" u ON u."Id" = f."FollowerId" AND {ActiveSeeker("u")}
          WHERE f."FolloweeId" = @me AND f."Status" = '{FollowStatuses.Accepted}'
            AND (@jg = '{JoinGenders.Anyone}' OR (@jg = '{JoinGenders.Men}' AND p."Gender" = '{SocialGenders.Male}')
                 OR (@jg = '{JoinGenders.Women}' AND p."Gender" = '{SocialGenders.Female}'))
            AND NOT {BlockedEitherWay("f.\"FollowerId\"", "@me")}
          ORDER BY f."CreatedAt" DESC
          LIMIT {SocialLimits.FollowedPostedMax}),
        ins AS (
          INSERT INTO "UserNotices" ("Id", "UserId", "Kind", "TripId", "ActorUserId", "RefId", "Amount", "Count", "CreatedAt", "UpdatedAt")
          SELECT gen_random_uuid(), x."FollowerId", '{NoticeKinds.FollowedPosted}', @trip, @me, NULL, NULL, 1, now(), now() FROM x
          ON CONFLICT ("UserId", "Kind", "TripId") DO NOTHING
          RETURNING "UserId")
        SELECT ins."UserId" AS "Value" FROM ins
        """;

    internal const string SaveCapsSql = """
        UPDATE "UserProfiles" SET "PublishTimes" = @publishes WHERE "UserId" = @me
        """;

    // Bell rows for the followers, then (FollowersImmediate) one push each in the same transaction. Returns the followers told.
    public static async Task<int> TellFollowersAsync(
        ApplicationDbContext db, INoticeWriter notices, Guid me, string posterName, Guid tripId, string joinGender, DateTime nowUtc, CancellationToken ct)
    {
        var told = await db.Database.SqlQueryRaw<Guid>(FollowersSql, Uuid("me", me), Uuid("trip", tripId), Text("jg", joinGender)).ToListAsync(ct);
        if (told.Count > 0 && PostNotifyRules.FollowersImmediate)
            await notices.PushAsync(told.Select(u => SocialPushCopy.TripPosted(u, me, tripId, posterName, nowUtc)).ToList(), ct);
        return told.Count;
    }

    // Called once per publishing call after the trip row is saved (create, or PUT /post on a trip that was not public). Sets
    // trip.NotifiedAt / trip.NotifyNearby (the caller saves the trip) and writes the rolling publish cap on the profile row.
    public static async Task<PostNotifyDto> OnPublishAsync(
        ApplicationDbContext db, INoticeWriter notices, Guid me, Poster poster, Trip trip, bool nearbyRequested, DateTime nowUtc, CancellationToken ct)
    {
        var publishes = PostNotifyRules.Append(poster.PublishTimes, nowUtc);
        var dto = new PostNotifyDto();
        if (trip.NotifiedAt is not null)
        {
            dto.Followers = PostNotifyStates.AlreadySent;
            dto.Nearby = PostNotifyStates.AlreadySent;
        }
        else
        {
            var nearby = PostNotifyRules.NearbyState(nearbyRequested, poster.IsPrivate, trip.StartDate, IstTime.TodayIst(nowUtc));
            if (nearby == PostNotifyStates.Queued && !PostNotifyRules.PosterFitsAudience(trip.JoinGender, poster.Gender))
                nearby = PostNotifyStates.Gender;
            var told = await TellFollowersAsync(db, notices, me, poster.Name, trip.Id, trip.JoinGender, nowUtc, ct);
            dto.Followers = told > 0 ? PostNotifyStates.Sent : PostNotifyStates.NoFollowers;
            dto.Nearby = nearby;
            trip.NotifyNearby = nearby == PostNotifyStates.Queued;
            if (told > 0 || trip.NotifyNearby) trip.NotifiedAt = nowUtc;
        }
        await db.Database.ExecuteSqlRawAsync(SaveCapsSql, [Uuid("me", me), Times("publishes", publishes)], ct);
        return dto;
    }

    private static NpgsqlParameter Times(string name, DateTime[] values)
        => new(name, NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = values.Select(Utc).ToArray() };
}
