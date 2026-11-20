using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Buddy.Be1;

// B3, B4, B8 to B11, B14, B17: every read is one statement (B8 with forTripId and B17: two). Keyset pages, projection only.
public sealed class BuddyListService(ApplicationDbContext db, IBuddyLiveHints hints, ILogger<BuddyListService> logger)
{
    private static readonly string ReceivedSql = $"""
        SELECT r."Id" AS "RequestId", r."CreatedAt", u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username"
        FROM "BuddyRequests" r JOIN "Users" u ON u."Id" = r."FromUserId"
        WHERE r."ToUserId" = @me AND r."Status" = '{BuddyRequestStatuses.Pending}' AND r."ExpiresAt" > now() AND u."IsActive"
          /*cursor*/
        ORDER BY r."CreatedAt" DESC, r."Id" DESC
        LIMIT @take
        """;

    private static readonly string SentSql = $"""
        SELECT r."Id" AS "RequestId", r."CreatedAt", r."ExpiresAt", now() AS "Now",
               u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username"
        FROM "BuddyRequests" r JOIN "Users" u ON u."Id" = r."ToUserId"
        WHERE r."FromUserId" = @me
          AND (r."Status" IN ('{BuddyRequestStatuses.Pending}', '{BuddyRequestStatuses.Declined}', '{BuddyRequestStatuses.Expired}')
               OR (r."Status" = '{BuddyRequestStatuses.ClosedByBlock}' AND r."BlockedSender"))
          AND r."ExpiresAt" > now() - {Be1Sql.Days(BuddyLimits.EndedSentShownFor)}
          /*cursor*/
        ORDER BY r."CreatedAt" DESC, r."Id" DESC
        LIMIT @take
        """;

    private const string CursorFilter = """AND (r."CreatedAt", r."Id") < (@at, @id)""";

    private const string BuddiesSql = """
        SELECT b."BuddyUserId" AS "UserId", COALESCE(u."Name", '') AS "Name", lower(COALESCE(u."Name", '')) AS "LowerName",
               u."Username", b."SeenAt", b."CreatedAt" AS "Since",
               (SELECT count(*) FROM "Buddies" c WHERE c."UserId" = @me)::int AS "Total",
               m."Status" AS "MemberStatus", m."InviteExpiresAt", m."RespondedAt", now() AS "Now"
        FROM "Buddies" b
        JOIN "Users" u ON u."Id" = b."BuddyUserId"
        LEFT JOIN "TripMembers" m ON m."TripId" = @trip AND m."UserId" = b."BuddyUserId"
        WHERE b."UserId" = @me
          /*cursor*/
        ORDER BY lower(COALESCE(u."Name", '')), b."BuddyUserId"
        LIMIT @take
        """;

    private const string NameCursorFilter = """AND (lower(COALESCE(u."Name", '')), b."BuddyUserId") > (@name, @id)""";

    private const string OwnsTripSql = """SELECT EXISTS (SELECT 1 FROM "Trips" t WHERE t."Id" = @trip AND t."OwnerUserId" = @me) AS "Value" """;

    private const string SeenSql = """
        UPDATE "Buddies" SET "SeenAt" = now() WHERE "UserId" = @me AND "BuddyUserId" = ANY(@ids) AND "SeenAt" IS NULL
        """;

    private const string RemoveSql = """
        DELETE FROM "Buddies" WHERE ("UserId" = @me AND "BuddyUserId" = @him) OR ("UserId" = @him AND "BuddyUserId" = @me)
        """;

    private static readonly string SummarySql = $"""
        SELECT (SELECT count(*) FROM "Buddies" b WHERE b."UserId" = @me)::int AS "Buddies",
               (SELECT count(*) FROM "Buddies" b WHERE b."UserId" = @me AND b."SeenAt" IS NULL)::int AS "NewBuddies",
               (SELECT count(*) FROM "BuddyRequests" r JOIN "Users" s ON s."Id" = r."FromUserId" AND s."IsActive"
                 WHERE r."ToUserId" = @me AND r."Status" = '{BuddyRequestStatuses.Pending}' AND r."ExpiresAt" > now())::int AS "ReceivedPending",
               (SELECT count(*) FROM "UserBlocks" ub WHERE ub."BlockerId" = @me)::int AS "Blocked"
        """;

    private const string BlocksSql = """
        SELECT ub."BlockedId" AS "UserId", ub."Id" AS "BlockId", ub."CreatedAt" AS "BlockedAt", COALESCE(u."Name", '') AS "Name", u."Username",
               (SELECT count(*) FROM "UserBlocks" c WHERE c."BlockerId" = @me)::int AS "Total"
        FROM "UserBlocks" ub JOIN "Users" u ON u."Id" = ub."BlockedId"
        WHERE ub."BlockerId" = @me
          /*cursor*/
        ORDER BY ub."CreatedAt" DESC, ub."Id" DESC
        LIMIT @take
        """;

    private const string BlockCursorFilter = """AND (ub."CreatedAt", ub."Id") < (@at, @id)""";

    private const string ContextPersonSql = """
        SELECT u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username"
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
                RequestId = r.RequestId, CreatedAt = r.CreatedAt, Person = Be1Sql.Person(r.UserId, r.Name, r.Username),
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
                    RequestId = r.RequestId, Person = Be1Sql.Person(r.UserId, r.Name, r.Username), State = state,
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
        return new CountedPageDto<BuddyItemDto>
        {
            Items = page.Select(r => new BuddyItemDto
            {
                UserId = r.UserId, Name = r.Name, Username = r.Username, Initial = MoneyPeople.Initial(r.Name),
                IsNew = r.SeenAt is null, Since = r.Since,
                TripState = forTripId is null ? null : BuddyRules.TripState(r.MemberStatus, r.InviteExpiresAt, r.RespondedAt, r.Now),
            }).ToList(),
            Count = rows.Count > 0 ? rows[0].Total : 0,
            NextCursor = rows.Count > limit && last is not null ? BuddyCursors.EncodeName(last.LowerName, last.UserId) : null,
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
        if (removed > 0) await hints.BuddiesChangedAsync([me, buddyUserId]);
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
            Person = Be1Sql.Person(person.UserId, person.Name, person.Username),
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
    }

    private sealed class BuddyRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string LowerName { get; set; } = string.Empty;
        public string? Username { get; set; }
        public DateTime? SeenAt { get; set; }
        public DateTime Since { get; set; }
        public int Total { get; set; }
        public string? MemberStatus { get; set; }
        public DateTime? InviteExpiresAt { get; set; }
        public DateTime? RespondedAt { get; set; }
        public DateTime Now { get; set; }
    }

    private sealed class BlockRow
    {
        public Guid UserId { get; set; }
        public Guid BlockId { get; set; }
        public DateTime BlockedAt { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public int Total { get; set; }
    }

    private sealed class PersonRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
    }

    private sealed class TripRow
    {
        public Guid TripId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string MyRole { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
    }
}
