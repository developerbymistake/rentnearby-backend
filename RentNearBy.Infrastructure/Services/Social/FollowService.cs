using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 1.3 / 2.3: follow, unfollow, follow requests, people lists. Every follow write takes the pair lock. A follow writes one
// Follows row (EffectsAt NULL) plus, when counted, the sender's FollowSendTimes; bell rows, push and counters come from
// FollowEffectsJob after commit (DESIGN 5). Every other follow change marks both users in FollowCountDirty.
public sealed class FollowService(ApplicationDbContext db, INoticeWriter notices, SocialAfterCommit afterCommit, ILogger<FollowService> logger,
    IFollowEffectsSignal? effects = null)
{
    private static readonly string TargetSql = $"""
        SELECT u."Id" AS "UserId", p."IsPrivate",
               (SELECT count(*) FROM "Follows" mf WHERE mf."FollowerId" = @me)::int AS "MyFollowing",
               (SELECT f."Status" FROM "Follows" f WHERE f."FollowerId" = @me AND f."FolloweeId" = u."Id") AS "Existing",
               {AcceptedFollow("@him", "@me")} AS "HeFollowsMe",
               COALESCE(mp."FollowSendTimes", ARRAY[]::timestamptz[]) AS "MySendTimes",
               mp."FollowBlockUntil" AS "MyBlockUntil"
        FROM "Users" u
        JOIN "UserProfiles" p ON p."UserId" = u."Id"
        LEFT JOIN "UserProfiles" mp ON mp."UserId" = @me
        WHERE u."Id" = @him AND {ActiveSeeker("u")} AND NOT {BlockedEitherWay("@me", "@him")}
        """;

    private const string InsertSql = """
        INSERT INTO "Follows" ("FollowerId", "FolloweeId", "Status", "CreatedAt", "AcceptedAt")
        VALUES (@me, @him, @status, now(), CASE WHEN @status = 'A' THEN now() END)
        ON CONFLICT DO NOTHING
        """;

    private const string SaveSendTimesSql = """
        UPDATE "UserProfiles" SET "FollowSendTimes" = @times WHERE "UserId" = @me
        """;

    private const string AcceptSql = """
        UPDATE "Follows" SET "Status" = 'A', "AcceptedAt" = now(), "EffectsAt" = COALESCE("EffectsAt", now())
        WHERE "FollowerId" = @him AND "FolloweeId" = @me AND "Status" = 'R'
        """;

    private const string DeclineSql = """
        DELETE FROM "Follows" WHERE "FollowerId" = @him AND "FolloweeId" = @me AND "Status" = 'R'
        """;

    private const string NotifyFollowsSql = """SELECT p."NotifyFollows" AS "Value" FROM "UserProfiles" p WHERE p."UserId" = @u""";

    // Without the Redis guard (tests, internal callers): the DB check alone decides.
    public async Task<BuddyResult<FollowStateDto>> FollowAsync(Guid me, Guid him, CancellationToken ct = default)
        => (await FollowAsync(me, him, false, ct)).Result;

    // mustBeExempt = Redis said FULL. The PgRetry lambda only RETURNS the outcome; FollowGuard.CompleteAsync acts on it once.
    public async Task<FollowWriteOutcome> FollowAsync(Guid me, Guid him, bool mustBeExempt, CancellationToken ct)
    {
        if (me == him) return new FollowWriteOutcome(BuddyResult<FollowStateDto>.Fail(SocialErrorTypes.PersonNotFound), FollowGuardOutcome.Nothing);
        var written = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            // Order sender -> pair. Under the sender lock the week/day window, the block and the FollowingMax count are exact.
            await BuddyLocks.LockSenderAsync(db, me, c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            var t = (await db.Database.SqlQueryRaw<TargetRow>(TargetSql, Uuid("me", me), Uuid("him", him)).ToListAsync(c)).FirstOrDefault();
            var now = Now();
            var decision = FollowWriteRules.Decide(
                t is not null, t?.Existing is not null, t?.MyBlockUntil is { } b ? Utc(b) : null,
                t is not null && t.MyFollowing >= SocialLimits.FollowingMax, t?.HeFollowsMe == true,
                t?.MySendTimes.Select(Utc), mustBeExempt, now);
            switch (decision.Kind)
            {
                case FollowWriteKind.NotFound:
                    return new FollowWriteOutcome(BuddyResult<FollowStateDto>.Fail(SocialErrorTypes.PersonNotFound), decision.Guard);
                case FollowWriteKind.Existing:
                    return new FollowWriteOutcome(State(t!.Existing!), decision.Guard);
                case FollowWriteKind.FollowingLimit:
                    return new FollowWriteOutcome(BuddyResult<FollowStateDto>.Fail(SocialErrorTypes.FollowingLimit), decision.Guard);
                case FollowWriteKind.Blocked:
                case FollowWriteKind.Full:
                    return new FollowWriteOutcome(BuddyResult<FollowStateDto>.Fail(SocialErrorTypes.TryLater), decision.Guard);
            }

            var status = t!.IsPrivate ? FollowStatuses.Requested : FollowStatuses.Accepted;
            var inserted = await db.Database.ExecuteSqlRawAsync(InsertSql, [Uuid("me", me), Uuid("him", him), Text("status", status)], c);
            if (inserted == 0)
                return new FollowWriteOutcome(State(status), new FollowGuardOutcome(false, false, true));
            if (decision.Counted)
                await db.Database.ExecuteSqlRawAsync(SaveSendTimesSql,
                    [Uuid("me", me), Times("times", FollowGuardRules.Append(t.MySendTimes.Select(Utc), now))], c);
            await tx.CommitAsync(c);
            return new FollowWriteOutcome(State(status), decision.Guard);
        }, ct, logger);
        if (written.Result.Ok) effects?.Wake();
        return written;
    }

    // Unfollow, or cancel my request. Always 200. True when a row was deleted (the handler then feeds the churn list).
    public async Task<bool> UnfollowAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        if (me == him) return false;
        var gone = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            var status = await DeleteFollowAsync(db, me, him, c);
            if (status != null)
            {
                await notices.DeleteAsync(NoticeKinds.FollowRequest, me, [him], c);
                await notices.DeleteAsync(NoticeKinds.NewFollower, me, [him], c);
                afterCommit.Hint(status == FollowStatuses.Accepted ? SocialLiveScopes.Followers : SocialLiveScopes.FollowRequests, him);
            }
            await tx.CommitAsync(c);
            return status != null;
        }, ct, logger);
        if (gone)
        {
            effects?.Wake();
            await afterCommit.FlushAsync();
        }
        return gone;
    }

    // Remove a follower (and his place in my My Buddies). Always 200.
    public async Task RemoveFollowerAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        if (me == him) return;
        var gone = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            var status = await DeleteFollowAsync(db, him, me, c);
            if (status != null)
            {
                await notices.DeleteAsync(NoticeKinds.NewFollower, him, [me], c);
                await notices.DeleteAsync(NoticeKinds.FollowRequest, him, [me], c);
                afterCommit.Hint(SocialLiveScopes.Followers, me);
            }
            await tx.CommitAsync(c);
            return status != null;
        }, ct, logger);
        if (!gone) return;
        effects?.Wake();
        await afterCommit.FlushAsync();
    }

    // The FollowAccepted bell and push stay inline (the owner's own action); EffectsAt is set so no "new follower" goes out later.
    public async Task<BuddyResult<FollowStateDto>> AcceptRequestAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            var changed = await db.Database.ExecuteSqlRawAsync(AcceptSql, [Uuid("me", me), Uuid("him", him)], c);
            if (changed == 0) return BuddyResult<FollowStateDto>.Fail(SocialErrorTypes.RequestGone);
            await MarkDirtyAsync(db, [me, him], c);
            await notices.DeleteAsync(NoticeKinds.FollowRequest, him, [me], c);
            await notices.UpsertAsync([new NoticeUpsert(him, NoticeKinds.FollowAccepted, me, me)], c);
            var notify = (await db.Database.SqlQueryRaw<bool>(NotifyFollowsSql, Uuid("u", him)).ToListAsync(c)).FirstOrDefault();
            if (notify)
            {
                var myName = await db.Users.Where(u => u.Id == me).Select(u => u.Name).FirstOrDefaultAsync(c);
                await notices.PushAsync([SocialPushCopy.FollowAccepted(him, me, myName ?? string.Empty)], c);
            }
            afterCommit.Hint(SocialLiveScopes.FollowRequests, me);
            afterCommit.Hint(SocialLiveScopes.Followers, me, him);
            afterCommit.Wake();
            await tx.CommitAsync(c);
            return BuddyResult<FollowStateDto>.Success(new FollowStateDto { State = FollowStates.Accepted });
        }, ct, logger);
        if (!result.Ok) return result;
        effects?.Wake();
        await afterCommit.FlushAsync();
        return result;
    }

    public async Task DeclineRequestAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        var changed = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            var n = await db.Database.ExecuteSqlRawAsync(DeclineSql, [Uuid("me", me), Uuid("him", him)], c);
            if (n > 0)
            {
                await MarkDirtyAsync(db, [me, him], c);
                await notices.DeleteAsync(NoticeKinds.FollowRequest, him, [me], c);
            }
            await tx.CommitAsync(c);
            return n;
        }, ct, logger);
        if (changed > 0) effects?.Wake();
    }

    private static BuddyResult<FollowStateDto> State(string status) => BuddyResult<FollowStateDto>.Success(
        new FollowStateDto { State = status == FollowStatuses.Accepted ? FollowStates.Following : FollowStates.Requested });

    private static NpgsqlParameter Times(string name, DateTime[] values)
        => new(name, NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = values.Select(Utc).ToArray() };

    // ------------------------------------------------------------------ lists (keyset (CreatedAt DESC, other id DESC))

    private const string NameFilter = """AND lower(COALESCE(u."Name", '')) LIKE @q""";

    private const string ListKeyset = """WHERE (x."CreatedAt", x."UserId") < (@at, @id)""";

    private static string ListSql(string direction, string status, bool withQuery) => $"""
        SELECT f."{(direction == "followers" ? "FollowerId" : "FolloweeId")}" AS "UserId", f."CreatedAt",
               count(*) OVER ()::int AS "Total"
        FROM "Follows" f
        JOIN "Users" u ON u."Id" = f."{(direction == "followers" ? "FollowerId" : "FolloweeId")}" AND {ActiveSeeker("u")}
        WHERE f."{(direction == "followers" ? "FolloweeId" : "FollowerId")}" = @me AND f."Status" = '{status}'
          {(withQuery ? NameFilter : string.Empty)}
        ORDER BY f."CreatedAt" DESC, u."Id" DESC
        """;

    private const string CountBuddiesSql = """
        SELECT count(*)::int AS "Value" FROM "Buddies" b JOIN "Users" u ON u."Id" = b."BuddyUserId" AND u."IsActive"
        WHERE b."UserId" = @me
        """;

    public async Task<FollowersPageDto> FollowersAsync(Guid me, (DateTime At, Guid Id)? cursor, int limit, string? q, CancellationToken ct = default)
    {
        var (ids, total, next) = await PageAsync("followers", FollowStatuses.Accepted, me, cursor, limit, q, ct);
        var buddies = (await db.Database.SqlQueryRaw<int>(CountBuddiesSql, Uuid("me", me)).ToListAsync(ct)).FirstOrDefault();
        return new FollowersPageDto { Items = await PersonRowsAsync(db, me, ids, ct), NextCursor = next, Total = total, Buddies = buddies };
    }

    public async Task<PeoplePageDto<PersonRowDto>> FollowingAsync(Guid me, (DateTime At, Guid Id)? cursor, int limit, CancellationToken ct = default)
    {
        var (ids, total, next) = await PageAsync("following", FollowStatuses.Accepted, me, cursor, limit, null, ct);
        return new PeoplePageDto<PersonRowDto> { Items = await PersonRowsAsync(db, me, ids, ct), NextCursor = next, Total = total };
    }

    public async Task<PeoplePageDto<FollowRequestRowDto>> RequestsAsync(Guid me, (DateTime At, Guid Id)? cursor, int limit, CancellationToken ct = default)
    {
        var rows = await RawPageAsync("followers", FollowStatuses.Requested, me, cursor, limit, null, ct);
        var page = rows.Take(limit).ToList();
        var people = await PersonRowsAsync(db, me, page.Select(r => r.UserId).ToList(), ct);
        var at = page.ToDictionary(r => r.UserId, r => r.CreatedAt);
        return new PeoplePageDto<FollowRequestRowDto>
        {
            Items = people.Select(p => new FollowRequestRowDto
            {
                UserId = p.UserId, Name = p.Name, Username = p.Username, PhotoUrl = p.PhotoUrl, Initial = p.Initial, HomeCity = p.HomeCity,
                FollowsMe = p.FollowsMe, IFollow = p.IFollow, FollowRequested = p.FollowRequested, IsBuddy = p.IsBuddy,
                MutualCount = p.MutualCount, MutualName = p.MutualName, RequestedAt = Utc(at[p.UserId]),
            }).ToList(),
            NextCursor = KeysetCursor.EncodeLast(page, rows.Count > limit, r => r.CreatedAt, r => r.UserId),
            Total = rows.Count > 0 ? rows[0].Total : 0,
        };
    }

    private async Task<(List<Guid> Ids, int Total, string? Next)> PageAsync(
        string direction, string status, Guid me, (DateTime At, Guid Id)? cursor, int limit, string? q, CancellationToken ct)
    {
        var rows = await RawPageAsync(direction, status, me, cursor, limit, q, ct);
        var page = rows.Take(limit).ToList();
        return (page.Select(r => r.UserId).ToList(), rows.Count > 0 ? rows[0].Total : 0,
            KeysetCursor.EncodeLast(page, rows.Count > limit, r => r.CreatedAt, r => r.UserId));
    }

    // The window total is counted over the whole list (keyset filter applied outside it).
    private async Task<List<ListRow>> RawPageAsync(
        string direction, string status, Guid me, (DateTime At, Guid Id)? cursor, int limit, string? q, CancellationToken ct)
    {
        var withQuery = !string.IsNullOrWhiteSpace(q);
        var sql = $"""
            SELECT x."UserId", x."CreatedAt", x."Total" FROM ({ListSql(direction, status, withQuery)}) x
            {(cursor is null ? string.Empty : ListKeyset)}
            ORDER BY x."CreatedAt" DESC, x."UserId" DESC
            LIMIT @take
            """;
        var ps = new List<object> { Uuid("me", me), Int("take", limit + 1) };
        if (withQuery) ps.Add(Text("q", LikePrefix(q!)));
        if (cursor is { } c)
        {
            ps.Add(At("at", c.At));
            ps.Add(Uuid("id", c.Id));
        }
        return await db.Database.SqlQueryRaw<ListRow>(sql, ps.ToArray()).ToListAsync(ct);
    }

    private static DateTime Now() => DateTime.UtcNow;

    private sealed class TargetRow
    {
        public Guid UserId { get; set; }
        public bool IsPrivate { get; set; }
        public int MyFollowing { get; set; }
        public string? Existing { get; set; }
        public bool HeFollowsMe { get; set; }
        public DateTime[] MySendTimes { get; set; } = [];
        public DateTime? MyBlockUntil { get; set; }
    }

    private sealed class ListRow
    {
        public Guid UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Total { get; set; }
    }
}

// The answer of one follow write and what the Redis guard must do with it (FollowGuard.CompleteAsync).
public sealed record FollowWriteOutcome(BuddyResult<FollowStateDto> Result, FollowGuardOutcome Guard);
