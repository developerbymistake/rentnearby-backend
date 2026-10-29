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
// Razorpay calls are sequential and never inside a DB transaction; each purchase gets its own scope.
public class PlanPurchaseReconcilerService(IServiceScopeFactory scopeFactory, ILogger<PlanPurchaseReconcilerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(7);
    private const int PageSize = 200;
    private const int ExpireBatch = 500;
    private const int LateCaptureEveryTicks = 30;

    private static readonly string[] OpenOrPaid = [PurchaseStatuses.Created, PurchaseStatuses.Pending, PurchaseStatuses.Paid];
    private static readonly string[] Closed = [PurchaseStatuses.Failed, PurchaseStatuses.Expired];

    private long _tick;
    private DateTime? _lateCursor;

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

            if (settled + expired + late > 0)
                logger.LogInformation("Plan reconciler: settled={Settled} expired={Expired} lateCaptures={Late}", settled, expired, late);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Plan reconciler run failed");
        }
    }

    private async Task<int> SettleAsync(string[] statuses, DateTime createdBefore, DateTime createdAfter, CancellationToken ct)
        => (await SettlePageAsync(statuses, createdBefore, createdAfter, newestFirst: false, ct)).Changed;

    private async Task<(int Changed, DateTime? Last, bool Full)> SettlePageAsync(
        string[] statuses, DateTime createdBefore, DateTime createdAfter, bool newestFirst, CancellationToken ct)
    {
        List<(Guid Id, DateTime CreatedAt)> rows;
        using (var scope = scopeFactory.CreateScope())
            rows = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().PlanPurchases
                .ListIdsForReconcileAsync(statuses, createdBefore, createdAfter, PageSize, newestFirst);

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
