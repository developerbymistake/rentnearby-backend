using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Buddy.Be1;

// B3, B4, B8 to B11, B14, B17: every read is one statement (B8: two, plus the shared mutuals read without forTripId or the owner check with it; B17: two). Keyset pages, projection only.
public sealed class BuddyListService(ApplicationDbContext db, IBuddyLiveHints hints, ILogger<BuddyListService> logger)
{
    internal static readonly string ReceivedSql = $"""
        SELECT r."Id" AS "RequestId", r."CreatedAt", u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username",
               u."ProfilePhotoUrl" AS "PhotoUrl"
        FROM "BuddyRequests" r JOIN "Users" u ON u."Id" = r."FromUserId"
        WHERE r."ToUserId" = @me AND r."Status" = '{BuddyRequestStatuses.Pending}' AND r."ExpiresAt" > now() AND u."IsActive" AND u."RoleId" = {TravellerOnly.SeekerRoleSql}
          /*cursor*/
        ORDER BY r."CreatedAt" DESC, r."Id" DESC
        LIMIT @take
        """;

    internal static readonly string SentSql = $"""
        SELECT r."Id" AS "RequestId", r."CreatedAt", r."ExpiresAt", now() AS "Now",
               u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username", u."ProfilePhotoUrl" AS "PhotoUrl"
        FROM "BuddyRequests" r JOIN "Users" u ON u."Id" = r."ToUserId" AND u."RoleId" = {TravellerOnly.SeekerRoleSql}
        WHERE r."FromUserId" = @me
          AND (r."Status" IN ('{BuddyRequestStatuses.Pending}', '{BuddyRequestStatuses.Declined}', '{BuddyRequestStatuses.Expired}')
               OR (r."Status" = '{BuddyRequestStatuses.ClosedByBlock}' AND r."BlockedSender"))
          AND r."ExpiresAt" > now() - {Be1Sql.Days(BuddyLimits.EndedSentShownFor)}
          /*cursor*/
        ORDER BY r."CreatedAt" DESC, r."Id" DESC
        LIMIT @take
        """;

    private const string CursorFilter = """AND (r."CreatedAt", r."Id") < (@at, @id)""";

    // Trips together = trips both of us are on (owner or Joined member); counted for the page rows only.
    internal const string BuddiesSql = $"""
        WITH page AS (
          SELECT b."BuddyUserId" AS "UserId", COALESCE(u."Name", '') AS "Name", lower(COALESCE(u."Name", '')) AS "LowerName",
                 u."Username", u."ProfilePhotoUrl" AS "PhotoUrl", b."SeenAt", b."CreatedAt" AS "Since",
                 (SELECT count(*) FROM "Buddies" c JOIN "Users" cu ON cu."Id" = c."BuddyUserId" AND cu."RoleId" = {TravellerOnly.SeekerRoleSql}
                   WHERE c."UserId" = @me)::int AS "Total",
                 m."Status" AS "MemberStatus", m."InviteExpiresAt", m."RespondedAt", now() AS "Now"
          FROM "Buddies" b
          JOIN "Users" u ON u."Id" = b."BuddyUserId" AND u."RoleId" = {TravellerOnly.SeekerRoleSql}
          LEFT JOIN "TripMembers" m ON m."TripId" = @trip AND m."UserId" = b."BuddyUserId"
          WHERE b."UserId" = @me
            /*cursor*/
          ORDER BY lower(COALESCE(u."Name", '')), b."BuddyUserId"
          LIMIT @take),
        mine AS (
          SELECT t."Id" FROM "Trips" t WHERE t."OwnerUserId" = @me
          UNION
          SELECT tm."TripId" FROM "TripMembers" tm WHERE tm."UserId" = @me AND tm."Status" = '{TripMemberStatuses.Joined}')
        SELECT page.*, gp."Gender",
               (SELECT count(*) FROM mine
                 WHERE mine."Id" IN (SELECT ht."Id" FROM "Trips" ht WHERE ht."OwnerUserId" = page."UserId"
                                     UNION ALL
                                     SELECT hm."TripId" FROM "TripMembers" hm
                                     WHERE hm."UserId" = page."UserId" AND hm."Status" = '{TripMemberStatuses.Joined}'))::int AS "TripsTogether"
        FROM page
        LEFT JOIN "UserProfiles" gp ON gp."UserId" = page."UserId"
        ORDER BY page."LowerName", page."UserId"
        """;

    private const string NameCursorFilter = """AND (lower(COALESCE(u."Name", '')), b."BuddyUserId") > (@name, @id)""";

    // Owner 2026-11-28 (I-13), GET /trips/{id}/invite-candidates: the people I follow (accepted, active seekers, no block either
    // way), my buddies first (Bucket 0) then by name; keyset on (Bucket, lower name, id).
    internal static readonly string CandidatesSql = $"""
        WITH c AS (
          SELECT f."FolloweeId" AS "UserId", COALESCE(u."Name", '') AS "Name", lower(COALESCE(u."Name", '')) AS "LowerName",
                 u."Username", u."ProfilePhotoUrl" AS "PhotoUrl", f."CreatedAt" AS "Since",
                 CASE WHEN EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @me AND b."BuddyUserId" = f."FolloweeId") THEN 0 ELSE 1 END AS "Bucket"
          FROM "Follows" f
          JOIN "Users" u ON u."Id" = f."FolloweeId" AND u."IsActive" AND u."RoleId" = {TravellerOnly.SeekerRoleSql}
          WHERE f."FollowerId" = @me AND f."Status" = '{FollowStatuses.Accepted}' AND f."FolloweeId" <> @me
            AND NOT {SocialSql.BlockedEitherWay("@me", "f.\"FolloweeId\"")}),
        page AS (
          SELECT c.*, (SELECT count(*) FROM c)::int AS "Total" FROM c
          WHERE TRUE /*cursor*/
          ORDER BY c."Bucket", c."LowerName", c."UserId"
          LIMIT @take)
        SELECT page.*, m."Status" AS "MemberStatus", m."InviteExpiresAt", m."RespondedAt", now() AS "Now", gp."Gender"
        FROM page
        LEFT JOIN "TripMembers" m ON m."TripId" = @trip AND m."UserId" = page."UserId"
        LEFT JOIN "UserProfiles" gp ON gp."UserId" = page."UserId"
        ORDER BY page."Bucket", page."LowerName", page."UserId"
        """;

    private const string CandidateCursorFilter = """AND (c."Bucket", c."LowerName", c."UserId") > (@bucket, @name, @id)""";

    // The candidate cursor reuses the name cursor; its name part starts with the bucket digit.
    public static string CandidateCursorName(int bucket, string lowerName) => $"{bucket}{lowerName}";

    public static bool TrySplitCandidateCursor((string LowerName, Guid Id) key, out int bucket, out string lowerName)
    {
        bucket = 0;
        lowerName = string.Empty;
        if (key.LowerName.Length == 0 || key.LowerName[0] is not ('0' or '1')) return false;
        bucket = key.LowerName[0] - '0';
        lowerName = key.LowerName[1..];
        return true;
    }

    private const string OwnsTripSql = """SELECT EXISTS (SELECT 1 FROM "Trips" t WHERE t."Id" = @trip AND t."OwnerUserId" = @me) AS "Value" """;

    private const string SeenSql = """
        UPDATE "Buddies" SET "SeenAt" = now() WHERE "UserId" = @me AND "BuddyUserId" = ANY(@ids) AND "SeenAt" IS NULL
        """;

    private const string RemoveSql = """
        DELETE FROM "Buddies" WHERE "UserId" = @me AND "BuddyUserId" = @him
        """;

    internal static readonly string SummarySql = $"""
        SELECT (SELECT count(*) FROM "Buddies" b JOIN "Users" bu ON bu."Id" = b."BuddyUserId" AND bu."RoleId" = {TravellerOnly.SeekerRoleSql}
                 WHERE b."UserId" = @me)::int AS "Buddies",
               (SELECT count(*) FROM "Buddies" b JOIN "Users" bu ON bu."Id" = b."BuddyUserId" AND bu."RoleId" = {TravellerOnly.SeekerRoleSql}
                 WHERE b."UserId" = @me AND b."SeenAt" IS NULL)::int AS "NewBuddies",
               (SELECT count(*) FROM "BuddyRequests" r JOIN "Users" s ON s."Id" = r."FromUserId" AND s."IsActive" AND s."RoleId" = {TravellerOnly.SeekerRoleSql}
                 WHERE r."ToUserId" = @me AND r."Status" = '{BuddyRequestStatuses.Pending}' AND r."ExpiresAt" > now())::int AS "ReceivedPending",
               (SELECT count(*) FROM "UserBlocks" ub WHERE ub."BlockerId" = @me)::int AS "Blocked"
        """;

    private const string BlocksSql = """
        SELECT ub."BlockedId" AS "UserId", ub."Id" AS "BlockId", ub."CreatedAt" AS "BlockedAt", COALESCE(u."Name", '') AS "Name", u."Username",
               u."ProfilePhotoUrl" AS "PhotoUrl",
               (SELECT count(*) FROM "UserBlocks" c WHERE c."BlockerId" = @me)::int AS "Total"
        FROM "UserBlocks" ub JOIN "Users" u ON u."Id" = ub."BlockedId"
        WHERE ub."BlockerId" = @me
          /*cursor*/
        ORDER BY ub."CreatedAt" DESC, ub."Id" DESC
        LIMIT @take
        """;

    private const string BlockCursorFilter = """AND (ub."CreatedAt", ub."Id") < (@at, @id)""";

    private const string ContextPersonSql = """
        SELECT u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."ProfilePhotoUrl" AS "PhotoUrl",
               CASE WHEN EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @me AND b."BuddyUserId" = @him)
                      OR EXISTS (SELECT 1 FROM "UserBlocks" mb WHERE mb."BlockerId" = @me AND mb."BlockedId" = @him)
                      OR EXISTS (SELECT 1 FROM "BuddyRequests" r
                                 WHERE r."PairLowId" = LEAST(@me, @him) AND r."PairHighId" = GREATEST(@me, @him))
                      OR EXISTS (SELECT 1 FROM "TripMembers" m JOIN "Trips" t ON t."Id" = m."TripId"
                                 WHERE (m."UserId" = @him AND t."OwnerUserId" = @me)
                                    OR (m."UserId" = @me AND t."OwnerUserId" = @him))
                    THEN u."Username" END AS "Username"
        FROM "Users" u
        WHERE u."Id" = @him AND u."IsActive"
          AND NOT EXISTS (SELECT 1 FROM "UserBlocks" ub WHERE ub."BlockerId" = @him AND ub."BlockedId" = @me)
        """;

    private static readonly string TripsInCommonSql = $"""
        SELECT t."Id" AS "TripId", t."Title", 'Owner'::text AS "MyRole", t."UpdatedAt"
        FROM "TripMembers" m JOIN "Trips" t ON t."Id" = m."TripId"
        WHERE m."UserId" = @him AND m."Status" = '{TripMemberStatuses.Joined}' AND t."OwnerUserId" = @me
        UNION ALL
        SELECT t."Id", t."Title", 'Viewer'::text, t."UpdatedAt"
        FROM "TripMembers" m JOIN "Trips" t ON t."Id" = m."TripId"
        WHERE m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Joined}' AND t."OwnerUserId" = @him
        ORDER BY "UpdatedAt" DESC
        """;

    public async Task<PageDto<ReceivedRequestDto>> ReceivedAsync(Guid me, (DateTime At, Guid Id)? cursor, int limit, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<ReceivedRow>(WithCursor(ReceivedSql, cursor is null ? null : CursorFilter),
            KeysetParams(me, cursor, limit)).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        return new PageDto<ReceivedRequestDto>
        {
            Items = page.Select(r => new ReceivedRequestDto
            {
                RequestId = r.RequestId, CreatedAt = r.CreatedAt, Person = Be1Sql.Person(r.UserId, r.Name, r.Username, r.PhotoUrl),
            }).ToList(),
            NextCursor = KeysetCursor.EncodeLast(page, rows.Count > limit, r => r.CreatedAt, r => r.RequestId),
        };
    }

    public async Task<PageDto<SentRequestDto>> SentAsync(Guid me, (DateTime At, Guid Id)? cursor, int limit, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<SentRow>(WithCursor(SentSql, cursor is null ? null : CursorFilter),
            KeysetParams(me, cursor, limit)).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        return new PageDto<SentRequestDto>
        {
            Items = page.Select(r =>
            {
                var state = BuddyRules.SentState(r.ExpiresAt, r.Now);
                return new SentRequestDto
                {
                    RequestId = r.RequestId, Person = Be1Sql.Person(r.UserId, r.Name, r.Username, r.PhotoUrl), State = state,
                    CreatedAt = r.CreatedAt, ExpiresAt = r.ExpiresAt, CanCancel = state == SentStates.Waiting,
                };
            }).ToList(),
            NextCursor = KeysetCursor.EncodeLast(page, rows.Count > limit, r => r.CreatedAt, r => r.RequestId),
        };
    }

    // null = forTripId given and the caller does not own that trip (404 TripNotFound).
    public async Task<CountedPageDto<BuddyItemDto>?> BuddiesAsync(
        Guid me, (string LowerName, Guid Id)? cursor, int limit, Guid? forTripId, CancellationToken ct = default)
    {
        if (forTripId is { } tripId)
        {
            var owns = await db.Database.SqlQueryRaw<bool>(OwnsTripSql, Be1Sql.Uuid("trip", tripId), Be1Sql.Uuid("me", me)).ToListAsync(ct);
            if (!owns.FirstOrDefault()) return null;
        }
        var sql = WithCursor(BuddiesSql, cursor is null ? null : NameCursorFilter);
        var ps = new List<object>
        {
            Be1Sql.Uuid("me", me), Be1Sql.Uuid("trip", forTripId ?? Guid.Empty), Be1Sql.Int("take", limit + 1),
        };
        if (cursor is { } c)
        {
            ps.Add(Be1Sql.Text("name", c.LowerName));
            ps.Add(Be1Sql.Uuid("id", c.Id));
        }
        var rows = await db.Database.SqlQueryRaw<BuddyRow>(sql, ps.ToArray()).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        var last = page.LastOrDefault();
        // The invite picker (forTripId) shows no mutual line, so it keeps its two statements.
        var mutuals = forTripId is null ? await SocialSql.MutualsAsync(db, me, page.Select(r => r.UserId).ToArray(), ct) : [];
        return new CountedPageDto<BuddyItemDto>
        {
            Items = page.Select(r => new BuddyItemDto
            {
                UserId = r.UserId, Name = r.Name, Username = r.Username, Initial = MoneyPeople.Initial(r.Name),
                PhotoUrl = CloudinaryThumb.Apply(r.PhotoUrl, CloudinaryThumb.PersonFace),
                IsNew = r.SeenAt is null, Since = r.Since, Gender = r.Gender, TripsTogether = r.TripsTogether,
                MutualCount = mutuals.TryGetValue(r.UserId, out var mu) ? mu.Count : 0,
                TripState = forTripId is null ? null : BuddyRules.TripState(r.MemberStatus, r.InviteExpiresAt, r.RespondedAt, r.Now),
            }).ToList(),
            Count = rows.Count > 0 ? rows[0].Total : 0,
            NextCursor = rows.Count > limit && last is not null ? BuddyCursors.EncodeName(last.LowerName, last.UserId) : null,
        };
    }

    // null = the caller does not own the trip, or a bad cursor shape (404 TripNotFound; the handler checks the cursor first).
    public async Task<CountedPageDto<BuddyItemDto>?> InviteCandidatesAsync(
        Guid me, Guid tripId, (string LowerName, Guid Id)? cursor, int limit, CancellationToken ct = default)
    {
        var owns = await db.Database.SqlQueryRaw<bool>(OwnsTripSql, Be1Sql.Uuid("trip", tripId), Be1Sql.Uuid("me", me)).ToListAsync(ct);
        if (!owns.FirstOrDefault()) return null;
        var ps = new List<object> { Be1Sql.Uuid("me", me), Be1Sql.Uuid("trip", tripId), Be1Sql.Int("take", limit + 1) };
        string? filter = null;
        if (cursor is { } c)
        {
            if (!TrySplitCandidateCursor(c, out var bucket, out var name)) return null;
            filter = CandidateCursorFilter;
            ps.Add(Be1Sql.Int("bucket", bucket));
            ps.Add(Be1Sql.Text("name", name));
            ps.Add(Be1Sql.Uuid("id", c.Id));
        }
        var rows = await db.Database.SqlQueryRaw<CandidateRow>(WithCursor(CandidatesSql, filter), ps.ToArray()).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        var last = page.LastOrDefault();
        return new CountedPageDto<BuddyItemDto>
        {
            Items = page.Select(r => new BuddyItemDto
            {
                UserId = r.UserId, Name = r.Name, Username = r.Username, Initial = MoneyPeople.Initial(r.Name),
                PhotoUrl = CloudinaryThumb.Apply(r.PhotoUrl, CloudinaryThumb.PersonFace),
                Since = r.Since, Gender = r.Gender, IsBuddy = r.Bucket == 0,
                TripState = BuddyRules.TripState(r.MemberStatus, r.InviteExpiresAt, r.RespondedAt, r.Now),
            }).ToList(),
            Count = rows.Count > 0 ? rows[0].Total : 0,
            NextCursor = rows.Count > limit && last is not null
                ? BuddyCursors.EncodeName(CandidateCursorName(last.Bucket, last.LowerName), last.UserId)
                : null,
        };
    }

    public async Task SeenAsync(Guid me, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return;
        await db.Database.ExecuteSqlRawAsync(SeenSql, [Be1Sql.Uuid("me", me), Be1Sql.Uuids("ids", userIds.Distinct().ToArray())], ct);
    }

    public async Task RemoveAsync(Guid me, Guid buddyUserId, CancellationToken ct = default)
    {
        if (me == buddyUserId) return;
        var removed = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, me, buddyUserId, c);
            var n = await db.Database.ExecuteSqlRawAsync(RemoveSql, [Be1Sql.Uuid("me", me), Be1Sql.Uuid("him", buddyUserId)], c);
            await tx.CommitAsync(c);
            return n;
        }, ct, logger);
        // My Buddies is my own list: only my row goes and the other person is never told.
        if (removed > 0) await hints.BuddiesChangedAsync([me]);
    }

    public async Task<BuddySummaryDto> SummaryAsync(Guid me, CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<BuddySummaryDto>(SummarySql, Be1Sql.Uuid("me", me)).ToListAsync(ct))[0];

    public async Task<CountedPageDto<BlockedPersonDto>> BlocksAsync(Guid me, (DateTime At, Guid Id)? cursor, int limit, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<BlockRow>(WithCursor(BlocksSql, cursor is null ? null : BlockCursorFilter),
            KeysetParams(me, cursor, limit)).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        return new CountedPageDto<BlockedPersonDto>
        {
            Items = page.Select(r => new BlockedPersonDto
            {
                UserId = r.UserId, Name = r.Name, Username = r.Username, Initial = MoneyPeople.Initial(r.Name), BlockedAt = r.BlockedAt,
                PhotoUrl = CloudinaryThumb.Apply(r.PhotoUrl, CloudinaryThumb.PersonFace),
            }).ToList(),
            Count = rows.Count > 0 ? rows[0].Total : 0,
            NextCursor = KeysetCursor.EncodeLast(page, rows.Count > limit, r => r.BlockedAt, r => r.BlockId),
        };
    }

    // null = person unknown, inactive or blocked-by-him (404 PersonNotFound).
    public async Task<BlockContextDto?> BlockContextAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        var person = (await db.Database.SqlQueryRaw<PersonRow>(ContextPersonSql, Be1Sql.Uuid("me", me), Be1Sql.Uuid("him", him))
            .ToListAsync(ct)).FirstOrDefault();
        if (person is null || me == him) return null;
        var trips = await db.Database.SqlQueryRaw<TripRow>(TripsInCommonSql, Be1Sql.Uuid("me", me), Be1Sql.Uuid("him", him)).ToListAsync(ct);
        return new BlockContextDto
        {
            Person = Be1Sql.Person(person.UserId, person.Name, person.Username, person.PhotoUrl),
            TripsInCommon = trips.Select(t => new TripInCommonDto { TripId = t.TripId, Title = t.Title, MyRole = t.MyRole }).ToList(),
        };
    }

    private static string WithCursor(string sql, string? filter) => sql.Replace("/*cursor*/", filter ?? string.Empty);

    private static object[] KeysetParams(Guid me, (DateTime At, Guid Id)? cursor, int limit)
    {
        var ps = new List<object> { Be1Sql.Uuid("me", me), Be1Sql.Int("take", limit + 1) };
        if (cursor is { } c)
        {
            ps.Add(Be1Sql.At("at", c.At));
            ps.Add(Be1Sql.Uuid("id", c.Id));
        }
        return ps.ToArray();
    }

    private sealed class ReceivedRow
    {
        public Guid RequestId { get; set; }
        public DateTime CreatedAt { get; set; }
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
    }

    private sealed class SentRow
    {
        public Guid RequestId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime Now { get; set; }
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
    }

    private sealed class BuddyRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string LowerName { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
        public DateTime? SeenAt { get; set; }
        public DateTime Since { get; set; }
        public int Total { get; set; }
        public string? MemberStatus { get; set; }
        public DateTime? InviteExpiresAt { get; set; }
        public DateTime? RespondedAt { get; set; }
        public DateTime Now { get; set; }
        public string? Gender { get; set; }
        public int TripsTogether { get; set; }
    }

    private sealed class CandidateRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string LowerName { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
        public DateTime Since { get; set; }
        public int Bucket { get; set; }
        public int Total { get; set; }
        public string? MemberStatus { get; set; }
        public DateTime? InviteExpiresAt { get; set; }
        public DateTime? RespondedAt { get; set; }
        public DateTime Now { get; set; }
        public string? Gender { get; set; }
    }

    private sealed class BlockRow
    {
        public Guid UserId { get; set; }
        public Guid BlockId { get; set; }
        public DateTime BlockedAt { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
        public int Total { get; set; }
    }

    private sealed class PersonRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
    }

    private sealed class TripRow
    {
        public Guid TripId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string MyRole { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
    }
}
