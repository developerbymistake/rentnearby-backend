using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Matrix L6 "on the first request": the owner's own non-Play row whose EndsAt passed is claimed now, with the same claim,
// effects and PlanExpired push as PlanLifecycleService (whichever comes first wins; the other gets 0 rows). Play rows wait for sync.
public sealed class PlanExpiryOnRead(
    ApplicationDbContext db, ILiveSlotService slots, IPlanCatalogCache catalog, IRabbitMqPublisher publisher,
    IListingCacheInvalidator invalidator, ILogger<PlanExpiryOnRead> logger)
{
    private sealed class Claimed
    {
        public string FeatureKey { get; set; } = string.Empty;
        public Guid PlanId { get; set; }
    }

    public const string DueSql = """
        SELECT EXISTS (SELECT 1 FROM "UserPlans"
                       WHERE "UserId" = {0} AND "Status" = 'Active' AND "EndsAt" <= now() AND "PlaySubscriptionId" IS NULL) AS "Value"
        """;

    public const string ClaimSql = """
        UPDATE "UserPlans" SET "Status" = 'Expired', "UpdatedAt" = now()
        WHERE "UserId" = {0} AND "Status" = 'Active' AND "EndsAt" <= now() AND "PlaySubscriptionId" IS NULL
        RETURNING "FeatureKey", "PlanId"
        """;

    // Returns the number of rows it expired. Never throws: a failure leaves the row to the 5-minute pass.
    public async Task<int> ExpireDueAsync(Guid userId, CancellationToken ct = default)
    {
        var notifications = new List<NotificationEvent>();
        var kinds = new HashSet<string>();
        try
        {
            if (db.Database.CurrentTransaction != null) return 0;
            if (!await db.Database.SqlQueryRaw<bool>(DueSql, userId).SingleAsync(ct)) return 0;

            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await slots.LockOwnerAsync(userId);
                var claimed = await db.Database.SqlQueryRaw<Claimed>(ClaimSql, userId).ToListAsync(ct);
                if (claimed.Count == 0)
                {
                    await tx.RollbackAsync(ct);
                    slots.DropAfterCommit();
                    return 0;
                }
                var snapshot = await catalog.GetAsync();
                foreach (var kind in claimed.Select(c => c.FeatureKey).Distinct())
                {
                    await slots.ExpireEffectsAsync(userId, kind);
                    kinds.Add(kind);
                }
                foreach (var row in claimed)
                {
                    var n = PlanNotifications.Expired(userId, snapshot.GetPlan(row.PlanId)?.Name ?? "Your plan");
                    db.NotificationEvents.Add(n);
                    notifications.Add(n);
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            slots.DropAfterCommit();
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Plan expiry on read failed for {UserId}; the lifecycle pass retries", userId);
            return 0;
        }

        await slots.AfterCommitAsync();
        foreach (var n in notifications)
            await PlanNotifications.PublishAsync(publisher, n, logger);
        foreach (var kind in kinds)
        {
            try
            {
                await invalidator.BustForUsersAsync([userId], kind);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Plan expiry on read cache bust failed");
            }
        }
        logger.LogInformation("Plan of user {UserId} expired on read ({Count} rows)", userId, notifications.Count);
        return notifications.Count;
    }
}
