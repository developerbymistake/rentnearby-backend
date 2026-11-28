using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// DESIGN 5.2 / 5.3 (discover): the follow transaction writes only the Follows row with EffectsAt NULL; that row is the outbox
// item. Each cycle, in its own short transactions: (1) claim new follows (bell rows, dirty marks, open push groups, EffectsAt),
// (2) recount dirty users from Follows, (3) send due push groups as one grouped push per receiver and kind. Every claim is
// FOR UPDATE SKIP LOCKED, so any number of instances can run it; a replay never double-counts (absolute recount) and never
// sends twice (outbox key per group).
public sealed class FollowEffectsWork(
    ApplicationDbContext db, INoticeWriter notices, SocialAfterCommit afterCommit, ILogger<FollowEffectsWork> logger)
{
    public const int ClaimBatch = 500, RecountBatch = 500, FlushBatch = 1000;

    internal const string ClaimSql = """
        SELECT f."FollowerId", f."FolloweeId", f."Status", f."CreatedAt"
        FROM "Follows" f
        WHERE f."EffectsAt" IS NULL
        ORDER BY f."CreatedAt"
        LIMIT @batch
        FOR UPDATE SKIP LOCKED
        """;

    internal const string DirtySql = """
        INSERT INTO "FollowCountDirty" ("UserId", "MarkedAt")
        SELECT u.id, now() FROM unnest(@users) AS u(id)
        WHERE EXISTS (SELECT 1 FROM "Users" x WHERE x."Id" = u.id)
        ORDER BY u.id
        ON CONFLICT ("UserId") DO UPDATE SET "MarkedAt" = EXCLUDED."MarkedAt"
        """;

    // One row per (receiver, kind) (grouped in C#: one INSERT ... ON CONFLICT cannot touch a row twice). A closed group opens with
    // DueAt = max(now + gather, last send + gap). An open group keeps its DueAt and only widens PendingSince to an older follow
    // claimed late by another instance; PendingSince never goes back past the last send, so a follow is never announced twice.
    internal static readonly string OpenGroupsSql = $"""
        INSERT INTO "FollowPushGroups" AS g ("ReceiverId", "Kind", "PendingSince", "DueAt", "LastSentAt")
        SELECT o.rid, o.kind, o.since, now() + {Interval(SocialPushCopy.FollowPushGather)}, NULL
        FROM unnest(@rids, @kinds, @sinces) AS o(rid, kind, since)
        WHERE EXISTS (SELECT 1 FROM "Users" x WHERE x."Id" = o.rid)
        ORDER BY o.rid, o.kind
        ON CONFLICT ("ReceiverId", "Kind") DO UPDATE SET
          "PendingSince" = CASE WHEN g."PendingSince" IS NULL
                                THEN GREATEST(EXCLUDED."PendingSince", COALESCE(g."LastSentAt", '-infinity'))
                                ELSE LEAST(g."PendingSince", GREATEST(EXCLUDED."PendingSince", COALESCE(g."LastSentAt", '-infinity'))) END,
          "DueAt" = CASE WHEN g."PendingSince" IS NULL
                         THEN GREATEST(now() + {Interval(SocialPushCopy.FollowPushGather)},
                                       COALESCE(g."LastSentAt", '-infinity') + {Interval(SocialPushCopy.FollowPushGap)})
                         ELSE g."DueAt" END
        """;

    internal const string MarkDoneSql = """
        UPDATE "Follows" f SET "EffectsAt" = now()
        FROM unnest(@followers, @followees) AS d(a, b)
        WHERE f."FollowerId" = d.a AND f."FolloweeId" = d.b AND f."EffectsAt" IS NULL
        """;

    // Every dirty writer upserts with DO UPDATE, so it holds the row lock until it commits and SKIP LOCKED leaves its row for the
    // next cycle. The count runs as a second statement (READ COMMITTED: fresh snapshot after the locks), never inside the DELETE.
    internal const string PickDirtySql = """
        DELETE FROM "FollowCountDirty" WHERE "UserId" IN (
          SELECT "UserId" FROM "FollowCountDirty" ORDER BY "UserId" LIMIT @batch FOR UPDATE SKIP LOCKED)
        RETURNING "UserId" AS "Value"
        """;

    internal static readonly string RecountSql = $"""
        UPDATE "UserProfiles" pr SET
          "FollowerCount" = (SELECT count(*)::int FROM "Follows" f WHERE f."FolloweeId" = pr."UserId" AND f."Status" = '{FollowStatuses.Accepted}'),
          "FollowingCount" = (SELECT count(*)::int FROM "Follows" f WHERE f."FollowerId" = pr."UserId" AND f."Status" = '{FollowStatuses.Accepted}')
        WHERE pr."UserId" = ANY(@ids)
        RETURNING pr."UserId" AS "Value"
        """;

    // Send-time truth from Follows: done effects only (a follow committed after this read waits for the next group), the kind's
    // status, active seeker followers not blocked either way. A followed-you group counts only rows accepted at insert
    // (AcceptedAt = CreatedAt): a request the receiver accepted himself is never announced as a new follower.
    private static string GroupWindow(string f) => $"""
        {f}."FolloweeId" = d."ReceiverId" AND {f}."Status" = d."Kind" AND {f}."CreatedAt" >= d."PendingSince"
          AND {f}."EffectsAt" IS NOT NULL
          AND (d."Kind" = '{FollowStatuses.Requested}' OR {f}."AcceptedAt" = {f}."CreatedAt")
          AND NOT {BlockedEitherWay($"{f}.\"FollowerId\"", "d.\"ReceiverId\"")}
        """;

    internal static readonly string FlushSql = $"""
        WITH due AS MATERIALIZED (
          SELECT g."ReceiverId", g."Kind", g."PendingSince"
          FROM "FollowPushGroups" g
          WHERE g."DueAt" <= now()
          ORDER BY g."DueAt"
          LIMIT @batch
          FOR UPDATE SKIP LOCKED)
        SELECT d."ReceiverId", d."Kind"::text AS "Kind", d."PendingSince",
               COALESCE(p."NotifyFollows", false) AND COALESCE(({ActiveSeeker("r")}), false) AS "Notify",
               COALESCE(c.n, 0) AS "Count", l."FollowerId" AS "LatestId", l."Name" AS "LatestName"
        FROM due d
        LEFT JOIN "Users" r ON r."Id" = d."ReceiverId"
        LEFT JOIN "UserProfiles" p ON p."UserId" = d."ReceiverId"
        LEFT JOIN LATERAL (
          SELECT count(*)::int AS n
          FROM "Follows" f JOIN "Users" u ON u."Id" = f."FollowerId" AND {ActiveSeeker("u")}
          WHERE {GroupWindow("f")}) c ON true
        LEFT JOIN LATERAL (
          SELECT f."FollowerId", u."Name"
          FROM "Follows" f JOIN "Users" u ON u."Id" = f."FollowerId" AND {ActiveSeeker("u")}
          WHERE {GroupWindow("f")}
          ORDER BY f."CreatedAt" DESC, f."FollowerId" DESC
          LIMIT 1) l ON true
        """;

    internal const string CloseGroupsSql = """
        UPDATE "FollowPushGroups" g SET "PendingSince" = NULL, "DueAt" = NULL,
          "LastSentAt" = CASE WHEN u.sent THEN now() ELSE g."LastSentAt" END
        FROM unnest(@rids, @kinds, @sent) AS u(rid, kind, sent)
        WHERE g."ReceiverId" = u.rid AND g."Kind" = u.kind
        """;

    // One cycle; true = some step filled its batch (run again at once).
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(ct);
        var recounted = await RecountAsync(ct);
        var flushed = await FlushGroupsAsync(ct);
        return claimed >= ClaimBatch || recounted >= RecountBatch || flushed >= FlushBatch;
    }

    // Step 1: new follows -> bell rows, dirty marks, open push groups, EffectsAt. Live hints after the commit.
    public async Task<int> ClaimAsync(CancellationToken ct)
    {
        var rows = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var batch = await db.Database.SqlQueryRaw<PendingFollow>(ClaimSql, Int("batch", ClaimBatch)).ToListAsync(c);
            if (batch.Count == 0) return batch;
            foreach (var r in batch) r.Status = r.Status.Trim();

            var dirty = batch.SelectMany(r => new[] { r.FollowerId, r.FolloweeId }).Distinct().ToArray();
            await db.Database.ExecuteSqlRawAsync(DirtySql, [Uuids("users", dirty)], c);

            await notices.UpsertAsync(NoticeRows(batch), c);

            var groups = OpenGroups(batch);
            await db.Database.ExecuteSqlRawAsync(OpenGroupsSql,
            [
                Uuids("rids", groups.Select(g => g.ReceiverId).ToArray()),
                Texts("kinds", groups.Select(g => g.Kind).ToArray()),
                Times("sinces", groups.Select(g => g.Since).ToArray()),
            ], c);

            await db.Database.ExecuteSqlRawAsync(MarkDoneSql,
                [Uuids("followers", batch.Select(r => r.FollowerId).ToArray()), Uuids("followees", batch.Select(r => r.FolloweeId).ToArray())], c);

            var accepted = batch.Where(r => r.Status == FollowStatuses.Accepted).Select(r => r.FolloweeId).Distinct().ToArray();
            var requested = batch.Where(r => r.Status == FollowStatuses.Requested).Select(r => r.FolloweeId).Distinct().ToArray();
            if (accepted.Length > 0) afterCommit.Hint(SocialLiveScopes.Followers, accepted);
            if (requested.Length > 0) afterCommit.Hint(SocialLiveScopes.FollowRequests, requested);
            await tx.CommitAsync(c);
            return batch;
        }, ct, logger);
        if (rows.Count > 0) await afterCommit.FlushAsync(ct);
        return rows.Count;
    }

    // Bell rows in key order (UserId, Kind, TripId), uuids compared as text = Postgres uuid order, so every writer locks UserNotices alike.
    public static List<NoticeUpsert> NoticeRows(IEnumerable<PendingFollow> batch) => batch
        .Select(r => new NoticeUpsert(r.FolloweeId,
            r.Status == FollowStatuses.Accepted ? NoticeKinds.NewFollower : NoticeKinds.FollowRequest, r.FollowerId, r.FollowerId))
        .OrderBy(n => n.UserId.ToString(), StringComparer.Ordinal).ThenBy(n => n.Kind, StringComparer.Ordinal)
        .ThenBy(n => n.TripId.ToString(), StringComparer.Ordinal)
        .ToList();

    // One row per (receiver, kind) with the oldest follow of the batch.
    public static IReadOnlyList<OpenGroup> OpenGroups(IEnumerable<PendingFollow> batch) => batch
        .GroupBy(r => (r.FolloweeId, r.Status))
        .Select(g => new OpenGroup(g.Key.FolloweeId, g.Key.Status, SocialSql.Utc(g.Min(r => r.CreatedAt))))
        .ToList();

    // Step 2: absolute recount of dirty users (idempotent), then a Followers hint so open profiles refresh their counters.
    public async Task<int> RecountAsync(CancellationToken ct)
    {
        afterCommit.Clear();
        var (picked, ids) = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, c);
            var gone = await db.Database.SqlQueryRaw<Guid>(PickDirtySql, Int("batch", RecountBatch)).ToListAsync(c);
            if (gone.Count == 0) return (0, new List<Guid>());
            var done = await db.Database.SqlQueryRaw<Guid>(RecountSql, Uuids("ids", gone)).ToListAsync(c);
            await tx.CommitAsync(c);
            return (gone.Count, done);
        }, ct, logger);
        if (ids.Count == 0) return picked;
        afterCommit.Hint(SocialLiveScopes.Followers, ids.ToArray());
        await afterCommit.FlushAsync(ct);
        return picked;
    }

    // Step 3: due groups -> one grouped push each (outbox, same transaction), then close the groups.
    public async Task<int> FlushGroupsAsync(CancellationToken ct)
    {
        var (groups, pushes) = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var due = await db.Database.SqlQueryRaw<DueGroup>(FlushSql, Int("batch", FlushBatch)).ToListAsync(c);
            if (due.Count == 0) return (0, 0);
            var specs = due.Select(Push).Where(p => p is not null).Select(p => p!).ToList();
            if (specs.Count > 0) await notices.PushAsync(specs, c);
            await db.Database.ExecuteSqlRawAsync(CloseGroupsSql,
            [
                Uuids("rids", due.Select(d => d.ReceiverId).ToArray()),
                Texts("kinds", due.Select(d => d.Kind.Trim()).ToArray()),
                Bools("sent", due.Select(d => Push(d) is not null).ToArray()),
            ], c);
            await tx.CommitAsync(c);
            return (due.Count, specs.Count);
        }, ct, logger);
        if (pushes > 0) notices.Wake();
        return groups;
    }

    // Null = nothing to send (no follower left in the window, follows pushes off, or the receiver is not an active seeker).
    public static PushSpec? Push(DueGroup g)
    {
        if (!g.Notify || g.Count <= 0 || g.PendingSince is not { } since) return null;
        return g.Kind.Trim() == FollowStatuses.Requested
            ? SocialPushCopy.RequestsGrouped(g.ReceiverId, g.Count, g.LatestName, Utc(since))
            : SocialPushCopy.FollowersGrouped(g.ReceiverId, g.Count, g.LatestId ?? Guid.Empty, g.LatestName, Utc(since));
    }

    private static NpgsqlParameter Times(string name, DateTime[] values)
        => new(name, NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = values.Select(Utc).ToArray() };

    private static NpgsqlParameter Bools(string name, bool[] values)
        => new(name, NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = values };

    public sealed record OpenGroup(Guid ReceiverId, string Kind, DateTime Since);

    public sealed class PendingFollow
    {
        public Guid FollowerId { get; set; }
        public Guid FolloweeId { get; set; }
        public string Status { get; set; } = FollowStatuses.Accepted;
        public DateTime CreatedAt { get; set; }
    }

    public sealed class DueGroup
    {
        public Guid ReceiverId { get; set; }
        public string Kind { get; set; } = FollowStatuses.Accepted;
        public DateTime? PendingSince { get; set; }
        public bool Notify { get; set; }
        public int Count { get; set; }
        public Guid? LatestId { get; set; }
        public string? LatestName { get; set; }
    }
}

// Polls every second and wakes on IFollowEffectsSignal after a follow commit; one scope per cycle.
public sealed class FollowEffectsJob(IServiceScopeFactory scopeFactory, IFollowEffectsSignal signal, ILogger<FollowEffectsJob> logger)
    : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1), ErrorPause = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested) // batch-loop: one cycle of set-based statements per tick
            {
                var more = false;
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    more = await scope.ServiceProvider.GetRequiredService<FollowEffectsWork>().RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "social.follow.effects cycle failed");
                    await Task.Delay(ErrorPause, stoppingToken);
                    continue;
                }
                if (!more) await signal.WaitAsync(Poll, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
