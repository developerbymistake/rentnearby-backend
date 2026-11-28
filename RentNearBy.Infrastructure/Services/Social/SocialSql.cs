using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Social;

// Parameters, shared SQL fragments and the people projections of the social services.
public static class SocialSql
{
    public static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    public static NpgsqlParameter NullUuid(string name, Guid? value) => new(name, NpgsqlDbType.Uuid) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Uuids(string name, IReadOnlyCollection<Guid> values)
        => new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values.ToArray() };

    public static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Text) { Value = value };

    public static NpgsqlParameter NullText(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Texts(string name, IReadOnlyCollection<string> values)
        => new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };

    public static NpgsqlParameter Int(string name, int value) => new(name, NpgsqlDbType.Integer) { Value = value };

    public static NpgsqlParameter Long(string name, long value) => new(name, NpgsqlDbType.Bigint) { Value = value };

    public static NpgsqlParameter Bool(string name, bool value) => new(name, NpgsqlDbType.Boolean) { Value = value };

    public static NpgsqlParameter Date(string name, DateOnly value) => new(name, NpgsqlDbType.Date) { Value = value };

    public static NpgsqlParameter At(string name, DateTime value)
        => new(name, NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(value, DateTimeKind.Utc) };

    public static NpgsqlParameter Double(string name, double value) => new(name, NpgsqlDbType.Double) { Value = value };

    public static string Interval(TimeSpan span) => $"interval '{span.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds'";

    // Active seeker account (a non-seeker or paused account reads as unknown).
    public static string ActiveSeeker(string alias) => $"""{alias}."IsActive" AND {alias}."RoleId" = {TravellerOnly.SeekerRoleSql}""";

    public static string BlockedEitherWay(string a, string b) =>
        $"""EXISTS (SELECT 1 FROM "UserBlocks" ub WHERE (ub."BlockerId" = {a} AND ub."BlockedId" = {b}) OR (ub."BlockerId" = {b} AND ub."BlockedId" = {a}))""";

    public static string AcceptedFollow(string follower, string followee) =>
        $"""EXISTS (SELECT 1 FROM "Follows" f WHERE f."FollowerId" = {follower} AND f."FolloweeId" = {followee} AND f."Status" = '{FollowStatuses.Accepted}')""";

    // Contract 2.6: a join request between a and b (either way) is Waiting, or was Declined in the last 30 days, with no Accepted
    // one between them: no new direct chat (403 CannotMessage). One rule for the chat start, the preflight and the profile button.
    public static string JoinBlocks(string a, string b) => $"""
        (EXISTS (SELECT 1 FROM "TripJoinRequests" j
                 WHERE ((j."RequesterId" = {a} AND j."OwnerUserId" = {b}) OR (j."RequesterId" = {b} AND j."OwnerUserId" = {a}))
                   AND (j."Status" = '{JoinRequestStatuses.Waiting}'
                        OR (j."Status" = '{JoinRequestStatuses.Declined}' AND j."DecidedAt" > now() - {Interval(SocialLimits.AskAgainAfter)})))
         AND NOT EXISTS (SELECT 1 FROM "TripJoinRequests" j
                         WHERE ((j."RequesterId" = {a} AND j."OwnerUserId" = {b}) OR (j."RequesterId" = {b} AND j."OwnerUserId" = {a}))
                           AND j."Status" = '{JoinRequestStatuses.Accepted}'))
        """;

    // "x is in owner's My Buddies" (directional rows, contract 1.4).
    public static string InBuddies(string owner, string x) =>
        $"""EXISTS (SELECT 1 FROM "Buddies" bb WHERE bb."UserId" = {owner} AND bb."BuddyUserId" = {x})""";

    // Spec 25: the caller accepted the CURRENT community guidelines (no profile = not accepted).
    private static readonly string GuidelinesSql = $"""
        SELECT EXISTS (SELECT 1 FROM "UserProfiles" p WHERE p."UserId" = @me AND p."GuidelinesAcceptedAt" IS NOT NULL
                       AND p."GuidelinesVersion" >= {SocialLimits.GuidelinesVersion}) AS "Value"
        """;

    public static async Task<bool> GuidelinesAcceptedAsync(ApplicationDbContext db, Guid me, CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<bool>(GuidelinesSql, Uuid("me", me)).ToListAsync(ct)).FirstOrDefault();

    // Write guard of every social write (403 ProfileRequired / SocialOnHold).
    private const string GuardSql = """
        SELECT EXISTS (SELECT 1 FROM "UserProfiles" p WHERE p."UserId" = @me) AS "HasProfile",
               (SELECT u."BuddyRequestsHeldUntil" FROM "Users" u WHERE u."Id" = @me) AS "HeldUntil",
               now() AS "Now"
        """;

    public sealed record GuardFail(string Type, object? Data);

    // touchesOther = follow, join send, room create, publish (the hold applies only to these).
    public static async Task<GuardFail?> GuardAsync(ApplicationDbContext db, Guid me, bool touchesOther, CancellationToken ct)
    {
        var g = (await db.Database.SqlQueryRaw<GuardRow>(GuardSql, Uuid("me", me)).ToListAsync(ct))[0];
        if (!g.HasProfile) return new GuardFail(SocialErrorTypes.ProfileRequired, null);
        if (touchesOther && g.HeldUntil is { } until && until > g.Now)
            return new GuardFail(SocialErrorTypes.SocialOnHold, new { until = DateTime.SpecifyKind(until, DateTimeKind.Utc) });
        return null;
    }

    private sealed class GuardRow
    {
        public bool HasProfile { get; set; }
        public DateTime? HeldUntil { get; set; }
        public DateTime Now { get; set; }
    }

    // One statement over the page ids: person columns, relation flags and the grouped mutual counts.
    internal static readonly string PersonRowsSql = $"""
        WITH ids AS (SELECT x.id, x.n FROM unnest(@ids) WITH ORDINALITY AS x(id, n)),
        m AS (
          SELECT b."FolloweeId" AS id, count(*)::int AS cnt,
                 (array_agg(COALESCE(mu."Name", '') ORDER BY a."CreatedAt" DESC))[1] AS first
          FROM "Follows" a
          JOIN "Follows" b ON b."FollowerId" = a."FolloweeId" AND b."Status" = '{FollowStatuses.Accepted}' AND b."FolloweeId" IN (SELECT id FROM ids)
          JOIN "Users" mu ON mu."Id" = a."FolloweeId" AND {ActiveSeeker("mu")}
          WHERE a."FollowerId" = @me AND a."Status" = '{FollowStatuses.Accepted}'
          GROUP BY b."FolloweeId")
        SELECT u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username", u."ProfilePhotoUrl" AS "PhotoUrl",
               p."HomeCityName" AS "HomeCity", p."Gender",
               EXISTS (SELECT 1 FROM "Follows" f WHERE f."FollowerId" = u."Id" AND f."FolloweeId" = @me AND f."Status" = '{FollowStatuses.Accepted}') AS "FollowsMe",
               EXISTS (SELECT 1 FROM "Follows" f WHERE f."FollowerId" = @me AND f."FolloweeId" = u."Id" AND f."Status" = '{FollowStatuses.Accepted}') AS "IFollow",
               EXISTS (SELECT 1 FROM "Follows" f WHERE f."FollowerId" = @me AND f."FolloweeId" = u."Id" AND f."Status" = '{FollowStatuses.Requested}') AS "FollowRequested",
               EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @me AND b."BuddyUserId" = u."Id") AS "IsBuddy",
               COALESCE(m.cnt, 0) AS "MutualCount", m.first AS "MutualName"
        FROM ids
        JOIN "Users" u ON u."Id" = ids.id
        LEFT JOIN "UserProfiles" p ON p."UserId" = u."Id"
        LEFT JOIN m ON m.id = u."Id"
        ORDER BY ids.n
        """;

    public static async Task<List<PersonRowDto>> PersonRowsAsync(ApplicationDbContext db, Guid me, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var rows = await db.Database.SqlQueryRaw<PersonRowRaw>(PersonRowsSql, Uuid("me", me), Uuids("ids", ids)).ToListAsync(ct);
        return rows.Select(r => new PersonRowDto
        {
            UserId = r.UserId, Name = r.Name, Username = r.Username, Initial = SocialRules.Initial(r.Name),
            PhotoUrl = Face(r.PhotoUrl), HomeCity = r.HomeCity, Gender = r.Gender,
            FollowsMe = r.FollowsMe, IFollow = r.IFollow, FollowRequested = r.FollowRequested, IsBuddy = r.IsBuddy,
            MutualCount = Math.Min(r.MutualCount, SocialLimits.MutualsCountCap),
            MutualName = r.MutualCount > 0 ? SocialRules.FirstName(r.MutualName) : null,
        }).ToList();
    }

    private sealed class PersonRowRaw
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
        public string? HomeCity { get; set; }
        public string? Gender { get; set; }
        public bool FollowsMe { get; set; }
        public bool IFollow { get; set; }
        public bool FollowRequested { get; set; }
        public bool IsBuddy { get; set; }
        public int MutualCount { get; set; }
        public string? MutualName { get; set; }
    }

    // Mutuals of many targets in one statement: people I follow who follow each target (count, first name, 3 photos).
    internal static readonly string MutualsSql = $"""
        SELECT x."Target", x."Name", x."PhotoUrl", x."Total" FROM (
          SELECT b."FolloweeId" AS "Target", COALESCE(mu."Name", '') AS "Name", mu."ProfilePhotoUrl" AS "PhotoUrl",
                 count(*) OVER (PARTITION BY b."FolloweeId")::int AS "Total",
                 row_number() OVER (PARTITION BY b."FolloweeId" ORDER BY a."CreatedAt" DESC, a."FolloweeId") AS rn
          FROM "Follows" a
          JOIN "Follows" b ON b."FollowerId" = a."FolloweeId" AND b."Status" = '{FollowStatuses.Accepted}' AND b."FolloweeId" = ANY(@targets)
          JOIN "Users" mu ON mu."Id" = a."FolloweeId" AND {ActiveSeeker("mu")}
          WHERE a."FollowerId" = @me AND a."Status" = '{FollowStatuses.Accepted}') x
        WHERE x.rn <= {SocialLimits.MutualsSample}
        ORDER BY x."Target", x.rn
        """;

    public static async Task<Dictionary<Guid, MutualsDto>> MutualsAsync(ApplicationDbContext db, Guid me, IReadOnlyCollection<Guid> targets, CancellationToken ct)
    {
        var list = targets.Where(t => t != me).Distinct().ToArray();
        if (list.Length == 0) return [];
        var rows = await db.Database.SqlQueryRaw<MutualRow>(MutualsSql, Uuid("me", me), Uuids("targets", list)).ToListAsync(ct);
        return rows.GroupBy(r => r.Target).ToDictionary(g => g.Key, g => new MutualsDto
        {
            Count = Math.Min(g.First().Total, SocialLimits.MutualsCountCap),
            Names = [SocialRules.FirstName(g.First().Name)],
            PhotoUrls = g.Select(r => Face(r.PhotoUrl)).OfType<string>().ToList(),
        });
    }

    private sealed class MutualRow
    {
        public Guid Target { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? PhotoUrl { get; set; }
        public int Total { get; set; }
    }

    public static string? Face(string? url) => CloudinaryThumb.Apply(url, CloudinaryThumb.PersonFace);

    public static SocialPersonDto Person(Guid id, string? name, string? username, string? photoUrl, string? homeCity, string? gender) => new()
    {
        UserId = id, Name = name ?? string.Empty, Username = username, Initial = SocialRules.Initial(name),
        PhotoUrl = Face(photoUrl), HomeCity = homeCity, Gender = gender,
    };

    // Travel level of a person block (spec B): the completed trips count and its level, computed here, never stored.
    public static T WithLevel<T>(T person, int completedTrips) where T : SocialPersonDto
    {
        person.CompletedTrips = Math.Max(0, completedTrips);
        person.Level = TravelLevels.Level(completedTrips);
        return person;
    }

    // Deletes one follow row and the buddy row it allowed (contract 1.3), and marks both users for the FollowEffectsJob recount
    // (DESIGN 5.2: no inline +1/-1 next to an absolute recount). Returns the deleted status or null.
    internal const string DeleteFollowSql = """
        WITH d AS (DELETE FROM "Follows" WHERE "FollowerId" = @a AND "FolloweeId" = @b RETURNING "Status"),
        dirty AS (INSERT INTO "FollowCountDirty" ("UserId", "MarkedAt")
                  SELECT DISTINCT x.id, now() FROM (VALUES (@a), (@b)) x(id) WHERE EXISTS (SELECT 1 FROM d)
                  ORDER BY x.id
                  ON CONFLICT ("UserId") DO UPDATE SET "MarkedAt" = EXCLUDED."MarkedAt"),
        bd AS (DELETE FROM "Buddies" WHERE "UserId" = @b AND "BuddyUserId" = @a)
        SELECT d."Status" AS "Value" FROM d
        """;

    // Users whose FollowerCount / FollowingCount FollowEffectsJob must recount; written in the same transaction as the Follows change.
    // Every dirty write upserts with DO UPDATE (holds the row lock until commit, so the recount skips it) in UserId order (no deadlock).
    internal const string MarkDirtySql = """
        INSERT INTO "FollowCountDirty" ("UserId", "MarkedAt")
        SELECT DISTINCT u.id, now() FROM unnest(@ids) AS u(id)
        ORDER BY u.id
        ON CONFLICT ("UserId") DO UPDATE SET "MarkedAt" = EXCLUDED."MarkedAt"
        """;

    public static Task<int> MarkDirtyAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> users, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(MarkDirtySql, [Uuids("ids", users)], ct);

    public static async Task<string?> DeleteFollowAsync(ApplicationDbContext db, Guid follower, Guid followee, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<string>(DeleteFollowSql, Uuid("a", follower), Uuid("b", followee)).ToListAsync(ct)).FirstOrDefault();

    // The one insert of a My Buddies row (contract 1.4, both paths: Add and join Accept): only while he follows me AND my list is
    // under BuddyLimits.MaxBuddies. Callers hold BuddyLocks.LockSenderAsync(me) so the count is exact. 1 = inserted, 0 = not a
    // follower, at the cap, or already there.
    internal static readonly string InsertBuddyCappedSql = $"""
        INSERT INTO "Buddies" ("UserId", "BuddyUserId", "CreatedAt", "SeenAt")
        SELECT @me, @him, now(), now()
        WHERE EXISTS (SELECT 1 FROM "Follows" f WHERE f."FollowerId" = @him AND f."FolloweeId" = @me AND f."Status" = '{FollowStatuses.Accepted}')
          AND (SELECT count(*) FROM "Buddies" b WHERE b."UserId" = @me) < {BuddyLimits.MaxBuddies}
        ON CONFLICT DO NOTHING
        """;

    public static Task<int> InsertBuddyCappedAsync(ApplicationDbContext db, Guid me, Guid him, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(InsertBuddyCappedSql, [Uuid("me", me), Uuid("him", him)], ct);

    // Escapes LIKE wildcards of user text.
    public static string LikeContains(string q)
        => "%" + q.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static string LikePrefix(string q)
        => q.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static int Clamp(int? limit) => limit is null ? SocialLimits.PageDefault : Math.Clamp(limit.Value, 1, SocialLimits.PageMax);

    public static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
