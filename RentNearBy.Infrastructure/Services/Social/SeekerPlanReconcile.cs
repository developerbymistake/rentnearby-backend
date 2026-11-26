using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

public sealed record SeekerPlanEffect(int InactiveBefore, int InactiveAfter, int PausedBefore, int PausedAfter, bool Changed)
{
    public static readonly SeekerPlanEffect None = new(0, 0, 0, 0, false);

    // Trips and posts newly taken off by this run (the "lost something" count of ExpireEffectsResult.Paused).
    public int Lost => Math.Max(0, InactiveAfter - InactiveBefore) + Math.Max(0, PausedAfter - PausedBefore);
}

public sealed record SeekerPlanCounts(int Active, int Paused);

// PLAN_STATES_SOCIAL 1-3: what the Seeker plan does to the owner's trips and posts after Plus ends and when it comes back.
// One idempotent function (SeekerPlanChoice); run by the plan pipeline inside its transaction under the Users lock
// (PlanSlotService grant and expiry effects) and lazily on read (EnsureAsync). Kept, never deleted.
public sealed class SeekerPlanReconcile(
    ApplicationDbContext db, ISeekerLimits seekerLimits, IBuddyLiveHints hints, ILogger<SeekerPlanReconcile> logger)
{
    private readonly HashSet<Guid> _owners = [];
    private readonly HashSet<Guid> _requesters = [];

    private sealed class RowSql
    {
        public Guid Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? PublishedAt { get; set; }
        public bool Open { get; set; }
        public bool Grandfathered { get; set; }
        public bool InactiveByPlan { get; set; }
        public bool IsPublic { get; set; }
        public bool PausedByPlan { get; set; }
        public bool Fresh { get; set; }
    }

    private sealed class CountsRow
    {
        public int Active { get; set; }
        public int Paused { get; set; }
    }

    internal static readonly string RowsSql = $"""
        SELECT t."Id", t."CreatedAt", t."PublishedAt",
               t."Status" IN ('{TripStatuses.Draft}', '{TripStatuses.Planned}') AS "Open",
               (t."CreatedAt" < COALESCE(p."CompletedAt", 'infinity')
                AND NOT EXISTS (SELECT 1 FROM "UserPlans" up JOIN "Plans" pl ON pl."Id" = up."PlanId"
                                WHERE up."UserId" = t."OwnerUserId" AND up."FeatureKey" = '{PlanFeatures.Seeker}'
                                  AND NOT pl."IsDefault" AND up."StartsAt" <= t."CreatedAt")) AS "Grandfathered",
               t."InactiveByPlan", t."IsPublic", t."PausedByPlan",
               ((t."StartDate" IS NULL OR t."StartDate" >= @today)
                AND (t."PostWindowMonths" IS NULL OR t."PublishedAt" + make_interval(months => t."PostWindowMonths"::int) >= now())) AS "Fresh"
        FROM "Trips" t LEFT JOIN "UserProfiles" p ON p."UserId" = t."OwnerUserId"
        WHERE t."OwnerUserId" = @me
          AND (t."Status" IN ('{TripStatuses.Draft}', '{TripStatuses.Planned}') OR t."InactiveByPlan" OR t."PausedByPlan")
        """;

    internal const string SetInactiveSql = """
        UPDATE "Trips" SET "InactiveByPlan" = @on WHERE "OwnerUserId" = @me AND "Id" = ANY(@ids) AND "InactiveByPlan" <> @on
        """;

    // Same statement shape as PublicTripService.UnpublishSql: waiting requests expire, their owner notices go, members stay.
    internal static readonly string PauseSql = $"""
        WITH t AS (
          UPDATE "Trips" SET "IsPublic" = FALSE, "PausedByPlan" = TRUE
          WHERE "OwnerUserId" = @me AND "Id" = ANY(@ids)
          RETURNING "Id"),
        r AS (
          UPDATE "TripJoinRequests" SET "Status" = '{JoinRequestStatuses.Expired}', "EndedAt" = now()
          WHERE "TripId" IN (SELECT "Id" FROM t) AND "Status" = '{JoinRequestStatuses.Waiting}'
          RETURNING "Id", "RequesterId"),
        n AS (DELETE FROM "UserNotices" WHERE "Kind" = '{NoticeKinds.JoinRequest}' AND "TripId" IN (SELECT "Id" FROM r))
        SELECT r."RequesterId" AS "Value" FROM r
        """;

    // Expired requests stay expired; the post fields were kept, so ck_trips_public_fields holds.
    internal const string RestoreSql = """
        UPDATE "Trips" SET "IsPublic" = TRUE, "PausedByPlan" = FALSE
        WHERE "OwnerUserId" = @me AND "Id" = ANY(@ids)
          AND "PublishedAt" IS NOT NULL AND "Seats" IS NOT NULL AND "PostDays" IS NOT NULL
        """;

    internal const string DropPausedSql = """
        UPDATE "Trips" SET "PausedByPlan" = FALSE WHERE "OwnerUserId" = @me AND "Id" = ANY(@ids)
        """;

    internal static readonly string CountsSql = $"""
        SELECT count(*) FILTER (WHERE t."Status" IN ('{TripStatuses.Draft}', '{TripStatuses.Planned}') AND NOT t."InactiveByPlan")::int AS "Active",
               count(*) FILTER (WHERE t."InactiveByPlan" OR t."PausedByPlan")::int AS "Paused"
        FROM "Trips" t WHERE t."OwnerUserId" = @me
        """;

    // Caller holds a transaction and the owner's Users row lock (plan grant, expiry, hold, refund, revoke, carry-over).
    public async Task<SeekerPlanEffect> ApplyAsync(Guid userId, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Seeker plan reconcile needs an open transaction.");
        var (rows, targets) = await ChooseAsync(userId, ct);
        var effect = new SeekerPlanEffect(
            rows.Count(r => r.InactiveByPlan), targets.InactiveAfter, rows.Count(r => r.PausedByPlan && r.Open && r.Fresh), targets.PausedAfter, targets.Changed);
        if (!targets.Changed) return effect;

        if (targets.Inactivate.Count > 0)
            await db.Database.ExecuteSqlRawAsync(SetInactiveSql, [Uuid("me", userId), Bool("on", true), Uuids("ids", targets.Inactivate)], ct);
        if (targets.Activate.Count > 0)
            await db.Database.ExecuteSqlRawAsync(SetInactiveSql, [Uuid("me", userId), Bool("on", false), Uuids("ids", targets.Activate)], ct);
        if (targets.PausePosts.Count > 0)
        {
            var requesters = await db.Database.SqlQueryRaw<Guid>(PauseSql, Uuid("me", userId), Uuids("ids", targets.PausePosts)).ToListAsync(ct);
            foreach (var r in requesters) _requesters.Add(r);
        }
        if (targets.RestorePosts.Count > 0)
            await db.Database.ExecuteSqlRawAsync(RestoreSql, [Uuid("me", userId), Uuids("ids", targets.RestorePosts)], ct);
        if (targets.DropPaused.Count > 0)
            await db.Database.ExecuteSqlRawAsync(DropPausedSql, [Uuid("me", userId), Uuids("ids", targets.DropPaused)], ct);

        _owners.Add(userId);
        logger.LogInformation("seeker.plan.reconciled {UserId} inactive {Inactive} paused {Paused}", userId, targets.InactiveAfter, targets.PausedAfter);
        return effect;
    }

    // Lazily on read (GET /social/me, GET /trips): a read without a change takes no lock; a drift is fixed under the Users lock.
    // Never throws: a failure leaves the rows to the next read or plan event.
    public async Task EnsureAsync(Guid userId, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction != null) return;
        try
        {
            var (_, targets) = await ChooseAsync(userId, ct);
            if (!targets.Changed) return;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("""SELECT 1 FROM "Users" WHERE "Id" = @me FOR UPDATE""", [Uuid("me", userId)], ct);
            await ApplyAsync(userId, ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DropAfterCommit();
            logger.LogWarning(ex, "seeker.plan.ensure failed for {UserId}; retried on the next read", userId);
            return;
        }
        await AfterCommitAsync();
    }

    // Owners whose trips a Seeker plan value change can move: any open (Draft/Planned) trip or any trip the plan already marked.
    private static readonly string AffectedOwnersSql = $"""
        SELECT DISTINCT t."OwnerUserId" AS "Value" FROM "Trips" t
        JOIN "Users" u ON u."Id" = t."OwnerUserId" AND u."RoleId" = {TravellerOnly.SeekerRoleSql}
        WHERE t."InactiveByPlan" OR t."PausedByPlan" OR t."Status" IN ('{TripStatuses.Draft}', '{TripStatuses.Planned}')
        """;

    // After an admin edit of a Seeker plan (values come from the refreshed catalog): every affected owner is reconciled now,
    // not on his next read. Each EnsureAsync takes no lock when nothing drifted and never throws. Returns the owners read.
    public async Task<int> ReconcileAllAsync(CancellationToken ct = default)
    {
        var owners = await db.Database.SqlQueryRaw<Guid>(AffectedOwnersSql).ToListAsync(ct);
        foreach (var owner in owners) await EnsureAsync(owner, ct); // batch-loop: one cheap read per owner, a lock only on drift
        return owners.Count;
    }

    private async Task<(List<SeekerPlanRow> Rows, SeekerPlanTargets Targets)> ChooseAsync(Guid userId, CancellationToken ct)
    {
        var limits = await seekerLimits.GetPlanAsync(userId, ct);
        var rows = (await db.Database.SqlQueryRaw<RowSql>(RowsSql, Uuid("me", userId), Date("today", IstTime.TodayIst(DateTime.UtcNow))).ToListAsync(ct))
            .Select(r => new SeekerPlanRow(r.Id, r.CreatedAt, r.PublishedAt, r.Open, r.Grandfathered, r.InactiveByPlan, r.IsPublic, r.PausedByPlan, r.Fresh))
            .ToList();
        return (rows, SeekerPlanChoice.Choose(rows, limits.ActiveTrips, limits.PublicTrips));
    }

    // Numbers of the end notice: open trips that stay active, trips inactive or with a paused post.
    public static async Task<SeekerPlanCounts> CountsAsync(ApplicationDbContext db, Guid userId, CancellationToken ct = default)
    {
        var row = (await db.Database.SqlQueryRaw<CountsRow>(CountsSql, Uuid("me", userId)).ToListAsync(ct))[0];
        return new SeekerPlanCounts(row.Active, row.Paused);
    }

    // After the caller's commit: the owner's posts and limits change, waiting requesters see their request expired. Never throws.
    public async Task AfterCommitAsync()
    {
        var owners = _owners.ToList();
        var requesters = _requesters.ToList();
        DropAfterCommit();
        if (owners.Count == 0) return;
        try
        {
            await hints.SocialChangedAsync(SocialLiveScopes.Posts, owners);
            await hints.SocialChangedAsync(SocialLiveScopes.Limits, owners);
            await hints.SocialChangedAsync(SocialLiveScopes.JoinRequests, owners);
            if (requesters.Count > 0) await hints.SocialChangedAsync(SocialLiveScopes.MyJoinRequests, requesters);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "seeker.plan.after_commit failed");
        }
    }

    public void DropAfterCommit()
    {
        _owners.Clear();
        _requesters.Clear();
    }
}
