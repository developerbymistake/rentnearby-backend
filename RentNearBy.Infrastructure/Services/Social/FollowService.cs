using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

// Contract 1.3 / 2.3: follow, unfollow, follow requests, people lists. Every follow write takes the pair lock; counters are
// updated inline in the same transaction.
public sealed class FollowService(ApplicationDbContext db, INoticeWriter notices, SocialAfterCommit afterCommit, ILogger<FollowService> logger)
{
    private static readonly string TargetSql = $"""
        SELECT u."Id" AS "UserId", p."IsPrivate", p."NotifyFollows",
               (SELECT count(*) FROM "Follows" mf WHERE mf."FollowerId" = @me)::int AS "MyFollowing",
               (SELECT f."Status" FROM "Follows" f WHERE f."FollowerId" = @me AND f."FolloweeId" = u."Id") AS "Existing",
               COALESCE(me."Name", '') AS "MyName"
        FROM "Users" u
        JOIN "UserProfiles" p ON p."UserId" = u."Id"
        JOIN "Users" me ON me."Id" = @me
        WHERE u."Id" = @him AND {ActiveSeeker("u")} AND NOT {BlockedEitherWay("@me", "@him")}
        """;

    private const string InsertSql = """
        INSERT INTO "Follows" ("FollowerId", "FolloweeId", "Status", "CreatedAt", "AcceptedAt")
        VALUES (@me, @him, @status, now(), CASE WHEN @status = 'A' THEN now() END)
        ON CONFLICT DO NOTHING
        """;

    private const string CountUpSql = """
        UPDATE "UserProfiles" SET "FollowingCount" = "FollowingCount" + CASE WHEN "UserId" = @a THEN 1 ELSE 0 END,
                                  "FollowerCount" = "FollowerCount" + CASE WHEN "UserId" = @b THEN 1 ELSE 0 END
        WHERE "UserId" IN (@a, @b)
        """;

    private const string AcceptSql = """
        UPDATE "Follows" SET "Status" = 'A', "AcceptedAt" = now()
        WHERE "FollowerId" = @him AND "FolloweeId" = @me AND "Status" = 'R'
        """;

    private const string DeclineSql = """
        DELETE FROM "Follows" WHERE "FollowerId" = @him AND "FolloweeId" = @me AND "Status" = 'R'
        """;

    private const string NotifyFollowsSql = """SELECT p."NotifyFollows" AS "Value" FROM "UserProfiles" p WHERE p."UserId" = @u""";

    public async Task<BuddyResult<FollowStateDto>> FollowAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        if (me == him) return BuddyResult<FollowStateDto>.Fail(SocialErrorTypes.PersonNotFound);
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            // Contract D6: the cap is a COUNT of my follow rows (accepted AND requested) under the sender lock; order sender -> pair.
            // Requested rows count from the start, so a later accept can never take me past the cap.
            await BuddyLocks.LockSenderAsync(db, me, c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            var t = (await db.Database.SqlQueryRaw<TargetRow>(TargetSql, Uuid("me", me), Uuid("him", him)).ToListAsync(c)).FirstOrDefault();
            if (t is null) return BuddyResult<FollowStateDto>.Fail(SocialErrorTypes.PersonNotFound);
            if (t.Existing is { } existing)
                return BuddyResult<FollowStateDto>.Success(new FollowStateDto { State = existing == FollowStatuses.Accepted ? FollowStates.Following : FollowStates.Requested });
            if (t.MyFollowing >= SocialLimits.FollowingMax) return BuddyResult<FollowStateDto>.Fail(SocialErrorTypes.FollowingLimit);

            var status = t.IsPrivate ? FollowStatuses.Requested : FollowStatuses.Accepted;
            await db.Database.ExecuteSqlRawAsync(InsertSql, [Uuid("me", me), Uuid("him", him), Text("status", status)], c);
            if (status == FollowStatuses.Accepted)
            {
                await db.Database.ExecuteSqlRawAsync(CountUpSql, [Uuid("a", me), Uuid("b", him)], c);
                await notices.UpsertAsync([new NoticeUpsert(him, NoticeKinds.NewFollower, me, me)], c);
                if (t.NotifyFollows) await notices.PushAsync([SocialPushCopy.NewFollower(him, me, t.MyName, Now())], c);
                afterCommit.Hint(SocialLiveScopes.Followers, him);
            }
            else
            {
                await notices.UpsertAsync([new NoticeUpsert(him, NoticeKinds.FollowRequest, me, me)], c);
                if (t.NotifyFollows) await notices.PushAsync([SocialPushCopy.FollowRequest(him, me, t.MyName)], c);
                afterCommit.Hint(SocialLiveScopes.FollowRequests, him);
            }
            afterCommit.Wake();
            await tx.CommitAsync(c);
            return BuddyResult<FollowStateDto>.Success(new FollowStateDto { State = status == FollowStatuses.Accepted ? FollowStates.Following : FollowStates.Requested });
        }, ct, logger);
        if (result.Ok) await afterCommit.FlushAsync();
        return result;
    }

    // Unfollow, or cancel my request. Always 200.
    public async Task UnfollowAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        if (me == him) return;
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
        if (gone) await afterCommit.FlushAsync();
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
        if (gone) await afterCommit.FlushAsync();
    }

    public async Task<BuddyResult<FollowStateDto>> AcceptRequestAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            var changed = await db.Database.ExecuteSqlRawAsync(AcceptSql, [Uuid("me", me), Uuid("him", him)], c);
            if (changed == 0) return BuddyResult<FollowStateDto>.Fail(SocialErrorTypes.RequestGone);
            await db.Database.ExecuteSqlRawAsync(CountUpSql, [Uuid("a", him), Uuid("b", me)], c);
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
        if (result.Ok) await afterCommit.FlushAsync();
        return result;
    }

    public async Task DeclineRequestAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            var changed = await db.Database.ExecuteSqlRawAsync(DeclineSql, [Uuid("me", me), Uuid("him", him)], c);
            if (changed > 0) await notices.DeleteAsync(NoticeKinds.FollowRequest, him, [me], c);
            await tx.CommitAsync(c);
        }, ct, logger);
    }

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
        public bool NotifyFollows { get; set; }
        public int MyFollowing { get; set; }
        public string? Existing { get; set; }
        public string MyName { get; set; } = string.Empty;
    }

    private sealed class ListRow
    {
        public Guid UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Total { get; set; }
    }
}
