using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Hourly (first run at startup), one scope per unit of work:
//   1. Expiry: each due Active plan is claimed with a conditional UPDATE ... RETURNING inside its own
//      transaction under the owner lock, then its listing effects run (plan-pause extra rooms/plots, ranks
//      back to free) and a PlanExpired inbox row is written. A renew that moved EndsAt first wins (0 rows).
//   2. Reminder: one claim of ReminderSentAt per plan (7 days ahead, half the term for short plans).
//   3. Daily: set-based rank reconcile per kind and listing-limit enforcement for rooms/plots (drift repair).
// Pushes go to notification.push after commit; caches are busted once per batch and kind.
// Play-owned rows (PlaySubscriptionId set) are expired by the Play sync, not by this clock. Two safety nets
// only: 72 h past EndsAt with Google unreachable (one sync first, claim only on ApiError), or a subscription
// that is no longer Mapped (claimed at once). A Replaced subscription waits for its successor's sync and only
// falls under the 72 h net. Reminders for Play rows go out only once auto-renew is off.
public class PlanLifecycleService(IServiceScopeFactory scopeFactory, IRabbitMqPublisher publisher, ILogger<PlanLifecycleService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReconcileEvery = TimeSpan.FromHours(24);
    private const int Batch = 500;

    private DateTime _lastReconcile = DateTime.MinValue;

    private sealed class DuePlan
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string FeatureKey { get; set; } = string.Empty;
        public Guid PlanId { get; set; }
        public DateTime EndsAt { get; set; }
        public Guid? PlaySubscriptionId { get; set; }
        public string? MappingStatus { get; set; }
        public string? PurchaseToken { get; set; }
    }

    private sealed class RemindPlan
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string FeatureKey { get; set; } = string.Empty;
        public Guid PlanId { get; set; }
        public DateTime EndsAt { get; set; }
        public Guid? PlaySubscriptionId { get; set; }
    }

    private enum PlayClaim { None, Stale, Detached }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var expired = await ExpirePassAsync(ct);
            var reminded = await ReminderPassAsync(ct);
            if (expired + reminded > 0)
                logger.LogInformation("Plan lifecycle: expired={Expired} reminded={Reminded}", expired, reminded);

            if (DateTime.UtcNow - _lastReconcile >= ReconcileEvery)
            {
                await ReconcilePassAsync();
                _lastReconcile = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Plan lifecycle run failed");
        }
    }

    // ---------------------------------------------------------------- pass 1: expiry

    private async Task<int> ExpirePassAsync(CancellationToken ct)
    {
        var total = 0;
        var skip = new HashSet<Guid>();
        while (!ct.IsCancellationRequested)
        {
            List<DuePlan> due;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                due = await db.Database.SqlQueryRaw<DuePlan>("""
                    SELECT up."Id", up."UserId", up."FeatureKey", up."PlanId", up."EndsAt", up."PlaySubscriptionId",
                           ps."MappingStatus", ps."PurchaseToken"
                    FROM "UserPlans" up
                    LEFT JOIN "PlaySubscriptions" ps ON ps."Id" = up."PlaySubscriptionId"
                    WHERE up."Status" = 'Active' AND (
                        (up."PlaySubscriptionId" IS NULL AND up."EndsAt" <= now())
                        OR (up."PlaySubscriptionId" IS NOT NULL AND (
                            up."EndsAt" <= now() - interval '72 hours'
                            OR (ps."MappingStatus" <> 'Mapped' AND ps."DetachedReason" IS DISTINCT FROM 'Replaced'))))
                    ORDER BY up."EndsAt"
                    LIMIT {0}
                    """, Batch).ToListAsync(ct);
            }
            due = due.Where(d => !skip.Contains(d.Id)).ToList();
            if (due.Count == 0) break;

            var expiredUsers = new Dictionary<string, HashSet<Guid>>();
            try
            {
                foreach (var plan in due)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await ExpireOneAsync(plan))
                    {
                        total++;
                        if (!expiredUsers.TryGetValue(plan.FeatureKey, out var users))
                            expiredUsers[plan.FeatureKey] = users = [];
                        users.Add(plan.UserId);
                    }
                    else skip.Add(plan.Id);
                }
            }
            finally
            {
                await BustBatchAsync(expiredUsers);
            }
            if (due.Count < Batch) break;
        }
        return total;
    }

    private async Task<bool> ExpireOneAsync(DuePlan plan)
    {
        NotificationEvent? notification = null;
        try
        {
            var playClaim = PlayClaim.None;
            if (plan.PlaySubscriptionId != null)
            {
                playClaim = await DecidePlayClaimAsync(plan);
                if (playClaim == PlayClaim.None) return false;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var slots = sp.GetRequiredService<ILiveSlotService>();
            var catalog = sp.GetRequiredService<IPlanCatalogCache>();

            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await slots.LockOwnerAsync(plan.UserId);
                var claimed = playClaim switch
                {
                    PlayClaim.Stale => await db.Database.SqlQueryRaw<Guid>("""
                        UPDATE "UserPlans" SET "Status" = 'Expired', "UpdatedAt" = now()
                        WHERE "Id" = {0} AND "Status" = 'Active' AND "PlaySubscriptionId" = {1}
                          AND "EndsAt" <= now() - interval '72 hours'
                        RETURNING "Id" AS "Value"
                        """, plan.Id, plan.PlaySubscriptionId.Value).ToListAsync(),
                    PlayClaim.Detached => await db.Database.SqlQueryRaw<Guid>("""
                        UPDATE "UserPlans" up SET "Status" = 'Expired', "UpdatedAt" = now()
                        WHERE up."Id" = {0} AND up."Status" = 'Active' AND up."PlaySubscriptionId" = {1}
                          AND EXISTS (SELECT 1 FROM "PlaySubscriptions" ps
                                      WHERE ps."Id" = up."PlaySubscriptionId" AND ps."MappingStatus" <> 'Mapped')
                        RETURNING up."Id" AS "Value"
                        """, plan.Id, plan.PlaySubscriptionId.Value).ToListAsync(),
                    _ => await db.Database.SqlQueryRaw<Guid>("""
                        UPDATE "UserPlans" SET "Status" = 'Expired', "UpdatedAt" = now()
                        WHERE "Id" = {0} AND "Status" = 'Active' AND "EndsAt" <= now() AND "PlaySubscriptionId" IS NULL
                        RETURNING "Id" AS "Value"
                        """, plan.Id).ToListAsync(),
                };
                if (claimed.Count == 0)
                {
                    await tx.RollbackAsync();
                    return false;
                }

                var effects = await slots.ExpireEffectsAsync(plan.UserId, plan.FeatureKey);
                var planName = (await catalog.GetAsync()).GetPlan(plan.PlanId)?.Name ?? "Your plan";
                notification = playClaim == PlayClaim.None
                    ? PlanNotifications.Expired(plan.UserId, planName)
                    : PlanNotifications.Ended(plan.UserId, planName, refunded: false);
                db.NotificationEvents.Add(notification);
                await db.SaveChangesAsync();
                await tx.CommitAsync();

                if (playClaim == PlayClaim.Stale)
                    logger.LogWarning(PlaySyncLogEvents.SweepStaleExpiry,
                        "Play plan {UserPlanId} of user {UserId} expired 72 h past EndsAt with Google unreachable", plan.Id, plan.UserId);
                else if (playClaim == PlayClaim.Detached)
                    logger.LogWarning(PlaySyncLogEvents.SweepDetachedExpiry,
                        "Play plan {UserPlanId} of user {UserId} expired: subscription {SubscriptionId} is {Status}",
                        plan.Id, plan.UserId, plan.PlaySubscriptionId, plan.MappingStatus);
                logger.LogInformation(
                    "Plan {UserPlanId} of user {UserId} expired ({Kind}): paused={Paused} reranked={Reranked}",
                    plan.Id, plan.UserId, plan.FeatureKey, effects.Paused, effects.Reranked);
            }

            await PlanNotifications.PublishAsync(publisher, notification, logger);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Plan expiry failed for {UserPlanId}; retried next run", plan.Id);
            return false;
        }
    }

    // Net (a): one sync first; only an unreachable Google (ApiError) lets the clock expire the row. Net (b):
    // the subscription is no longer Mapped (also after a sync that just detached or rejected it).
    private async Task<PlayClaim> DecidePlayClaimAsync(DuePlan plan)
    {
        if (plan.MappingStatus != PlayMappingStatuses.Mapped) return PlayClaim.Detached;

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        PlaySyncOutcome outcome;
        try
        {
            outcome = (await sp.GetRequiredService<IPlayBillingSyncService>()
                .SyncTokenAsync(new PlaySyncRequest(plan.PurchaseToken!, PurchaseSources.PlayReconcile))).Outcome;
        }
        catch (PlanServiceException)
        {
            outcome = PlaySyncOutcome.ApiError;
        }
        if (outcome == PlaySyncOutcome.ApiError) return PlayClaim.Stale;

        var status = await sp.GetRequiredService<ApplicationDbContext>().PlaySubscriptions.AsNoTracking()
            .Where(s => s.Id == plan.PlaySubscriptionId)
            .Select(s => s.MappingStatus)
            .FirstOrDefaultAsync();
        return status is null or PlayMappingStatuses.Mapped ? PlayClaim.None : PlayClaim.Detached;
    }

    // One DISTINCT district query and one bust per kind for the whole batch.
    private async Task BustBatchAsync(Dictionary<string, HashSet<Guid>> expiredUsers)
    {
        if (expiredUsers.Count == 0) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var invalidator = scope.ServiceProvider.GetRequiredService<IListingCacheInvalidator>();
            foreach (var (kind, users) in expiredUsers)
                await invalidator.BustForUsersAsync(users, kind);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Plan expiry cache bust failed");
        }
    }

    // ---------------------------------------------------------------- pass 2: reminder

    private async Task<int> ReminderPassAsync(CancellationToken ct)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var notifications = new List<NotificationEvent>();
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var snapshot = await scope.ServiceProvider.GetRequiredService<IPlanCatalogCache>().GetAsync();

                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var claimed = await db.Database.SqlQueryRaw<RemindPlan>("""
                    UPDATE "UserPlans" SET "ReminderSentAt" = now()
                    WHERE "Id" IN (
                        SELECT "Id" FROM "UserPlans"
                        WHERE "Status" = 'Active' AND "ReminderSentAt" IS NULL
                          AND "EndsAt" > now() AND "EndsAt" <= now() + interval '7 days'
                          AND "EndsAt" <= now() + LEAST(interval '7 days', ("EndsAt" - "StartsAt") / 2)
                          AND ("PlaySubscriptionId" IS NULL OR "PlayAutoRenewEnabled" = false)
                        LIMIT {0})
                      AND "Status" = 'Active' AND "ReminderSentAt" IS NULL AND "EndsAt" > now()
                      AND ("PlaySubscriptionId" IS NULL OR "PlayAutoRenewEnabled" = false)
                    RETURNING "Id", "UserId", "FeatureKey", "PlanId", "EndsAt", "PlaySubscriptionId"
                    """, Batch).ToListAsync(ct);
                if (claimed.Count == 0)
                {
                    await tx.RollbackAsync(ct);
                    break;
                }

                foreach (var p in claimed)
                {
                    var name = snapshot.GetPlan(p.PlanId)?.Name ?? "Your plan";
                    var n = p.PlaySubscriptionId == null
                        ? PlanNotifications.Expiring(p.UserId, name, p.EndsAt)
                        : PlanNotifications.ExpiringCancelled(p.UserId, name, p.EndsAt);
                    db.NotificationEvents.Add(n);
                    notifications.Add(n);
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                total += claimed.Count;

                foreach (var n in notifications)
                    await PlanNotifications.PublishAsync(publisher, n, logger);
                if (claimed.Count < Batch) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Plan reminder batch failed; retried next run");
                break;
            }
        }
        return total;
    }

    // ---------------------------------------------------------------- pass 3: rank and limit reconcile

    private async Task ReconcilePassAsync()
    {
        foreach (var kind in PlanFeatures.All)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var slots = scope.ServiceProvider.GetRequiredService<ILiveSlotService>();
                var invalidator = scope.ServiceProvider.GetRequiredService<IListingCacheInvalidator>();
                var result = await slots.ReconcileRanksAsync(kind);
                if (result.Rows > 0)
                {
                    logger.LogWarning("Rank reconcile fixed {Rows} {Kind} rows (drift)", result.Rows, kind);
                    await invalidator.BustDistrictsAsync(kind, result.DistrictIds);
                }

                if (PlanFeatures.HasListingSlots(kind))
                {
                    var r = await slots.EnforceLimitsAsync(kind);
                    if (r.Paused > 0)
                    {
                        logger.LogWarning("Limit reconcile plan-paused {Rows} {Kind} rows (drift)", r.Paused, kind);
                        await invalidator.BustForUsersAsync(r.Owners, kind);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rank reconcile failed for {Kind}", kind);
            }
        }
    }
}
