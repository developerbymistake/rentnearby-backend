using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// Source of truth next to the webhook: every 2 minutes (first run at startup) it asks Razorpay about open
// and paid purchases and settles them through the same idempotent ApplyCaptured path.
//   A: Created/Pending/Paid older than 2 min -> captured: grant; Paid + RoleMismatch/QuoteExpired: auto refund
//      (Paid + AmountMismatch is left to the admin).
//   B: Created/Pending past ExpiresAt -> Expired, coupon released.
//   C: every 30th tick, Failed/Expired from the last 7 days -> a late capture still settles.
//   D: every 15th tick, Paid older than 7 days, oldest first one page per pass -> a blocked grant whose
//      auto refund kept failing is still refunded.
// Razorpay calls are sequential and never inside a DB transaction; each purchase gets its own scope.
// The Razorpay passes skip Provider=Play rows. Google Play passes (contract 3.7), all through SyncTokenAsync:
//   P1: queued server actions (cancel/revoke), exponential backoff capped at 60 min, never dropped.
//   P2: entitled-looking tokens past their expiry. P3: acknowledgement retry. P4 (every 30th tick): tokens not
//   synced for 24 h. P5 (every 720th tick): voided purchases of the last 30 days. P6 = the open-order expiry above.
public class PlanPurchaseReconcilerService(IServiceScopeFactory scopeFactory, ILogger<PlanPurchaseReconcilerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(7);
    private static readonly TimeSpan AckOverdueAfter = TimeSpan.FromDays(2);
    private const int PageSize = 200;
    private const int ExpireBatch = 500;
    private const int LateCaptureEveryTicks = 30;
    private const int BlockedRetryEveryTicks = 15;
    private const int PlayActionBatch = 100;
    private const int PlayStaleEveryTicks = 30;
    private const int PlayVoidedEveryTicks = 720;

    private static readonly string[] OpenOrPaid = [PurchaseStatuses.Created, PurchaseStatuses.Pending, PurchaseStatuses.Paid];
    private static readonly string[] Closed = [PurchaseStatuses.Failed, PurchaseStatuses.Expired];

    private long _tick;
    private DateTime? _lateCursor;
    private DateTime? _blockedCursor;

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
        var tick = _tick++;
        try
        {
            var now = DateTime.UtcNow;
            var settled = await SettleAsync(OpenOrPaid, now - MinAge, now - LookBack, ct);

            int expired;
            using (var scope = scopeFactory.CreateScope())
                expired = await scope.ServiceProvider.GetRequiredService<IPlanPurchaseService>().ExpireStaleOpenAsync(ExpireBatch);

            var late = 0;
            if (tick % LateCaptureEveryTicks == 0)
            {
                // Newest first, walking back one page per pass so every row in the window is reached.
                var before = _lateCursor ?? now - MinAge;
                var (count, last, full) = await SettlePageAsync(Closed, before, now - LookBack, newestFirst: true, ct);
                late = count;
                _lateCursor = full ? last : null;
            }

            var blockedRetried = 0;
            if (tick % BlockedRetryEveryTicks == 0)
            {
                var (count, last, full) = await SettlePageAsync(
                    [PurchaseStatuses.Paid], now - LookBack, _blockedCursor, newestFirst: false, ct);
                blockedRetried = count;
                _blockedCursor = full ? last : null;
            }

            if (settled + expired + late + blockedRetried > 0)
                logger.LogInformation(
                    "Plan reconciler: settled={Settled} expired={Expired} lateCaptures={Late} blockedRetried={Blocked}",
                    settled, expired, late, blockedRetried);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Plan reconciler run failed");
        }

        await RunPlayPassesAsync(tick, ct);
    }

    private async Task RunPlayPassesAsync(long tick, CancellationToken ct)
    {
        try
        {
            using var probe = scopeFactory.CreateScope();
            if (!probe.ServiceProvider.GetRequiredService<IPlayBillingClient>().IsConfigured) return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Play reconciler could not start");
            return;
        }

        var actions = await PlayPassAsync("P1 server actions", s => s.RunPendingServerActionsAsync(PlayActionBatch), ct);
        var due = await PlayPassAsync("P2 due", s => s.SyncDueAsync(PageSize), ct);
        var acked = await AckPassAsync(ct);
        var stale = tick % PlayStaleEveryTicks == 0 ? await PlayPassAsync("P4 stale", s => s.SyncStaleAsync(PageSize), ct) : 0;
        var voided = tick % PlayVoidedEveryTicks == 0 ? await PlayPassAsync("P5 voided", s => s.ReconcileVoidedAsync(), ct) : 0;

        if (actions + due + acked + stale + voided > 0)
            logger.LogInformation(
                "Play reconciler: actions={Actions} due={Due} acked={Acked} stale={Stale} voided={Voided}",
                actions, due, acked, stale, voided);
    }

    private async Task<int> PlayPassAsync(string name, Func<IPlayBillingSyncService, Task<int>> pass, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return 0;
        try
        {
            using var scope = scopeFactory.CreateScope();
            return await pass(scope.ServiceProvider.GetRequiredService<IPlayBillingSyncService>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Play reconciler pass {Pass} failed", name);
            return 0;
        }
    }

    // P3: acknowledgement retry; Google refunds an unacknowledged purchase after 3 days.
    private async Task<int> AckPassAsync(CancellationToken ct)
    {
        List<Guid> ids;
        try
        {
            using var scope = scopeFactory.CreateScope();
            ids = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().PlaySubscriptions.ListAckPendingAsync(PageSize);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Play reconciler pass P3 ack failed");
            return 0;
        }

        var acked = 0;
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                if (await sp.GetRequiredService<IPlayEntitlementApplier>().TryAcknowledgeAsync(id))
                {
                    acked++;
                    continue;
                }
                var sub = await sp.GetRequiredService<IUnitOfWork>().PlaySubscriptions.GetByIdAsync(id);
                if (sub?.StartTime is { } start && start < DateTime.UtcNow - AckOverdueAfter && sub.AcknowledgedAt == null)
                    logger.LogError(PlaySyncLogEvents.AckOverdue,
                        "Play purchase token {TokenTail} (subscription {SubscriptionId}) still unacknowledged since {Start}; Google refunds it at 3 days",
                        PlaySyncLogEvents.Tail(sub.PurchaseToken), id, start);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Play acknowledge retry failed for subscription {SubscriptionId}", id);
            }
        }
        return acked;
    }

    private async Task<int> SettleAsync(string[] statuses, DateTime createdBefore, DateTime? createdAfter, CancellationToken ct)
        => (await SettlePageAsync(statuses, createdBefore, createdAfter, newestFirst: false, ct)).Changed;

    private async Task<(int Changed, DateTime? Last, bool Full)> SettlePageAsync(
        string[] statuses, DateTime createdBefore, DateTime? createdAfter, bool newestFirst, CancellationToken ct)
    {
        List<(Guid Id, DateTime CreatedAt)> rows;
        using (var scope = scopeFactory.CreateScope())
            rows = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().PlanPurchases
                .ListIdsForReconcileAsync(statuses, createdBefore, createdAfter, PageSize, newestFirst, providerNot: PaymentProviders.Play);

        var changed = 0;
        foreach (var (id, _) in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopeFactory.CreateScope();
                var outcome = await scope.ServiceProvider.GetRequiredService<IPlanPurchaseService>()
                    .ReconcileOrderAsync(id, PurchaseSources.Reconcile);
                if (outcome is ReconcileOutcome.Granted or ReconcileOutcome.Refunded) changed++;
            }
            catch (PlanServiceException ex)
            {
                logger.LogWarning("Plan reconciler: purchase {PurchaseId} {Type}: {Message}", id, ex.ErrorType, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Plan reconciler: purchase {PurchaseId} failed", id);
            }
        }
        return (changed, rows.Count > 0 ? rows[^1].CreatedAt : null, rows.Count == PageSize);
    }
}
