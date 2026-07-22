using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Standalone sweep for CoinPackPurchase — does NOT extend PendingPaymentCleanupService (which swept
// the now-deleted PaymentTransaction table). Three passes per run:
//   Pass A self-heals a purchase whose wallet credit already succeeded but whose own Status flip
//     never landed (a crash between CoinPackPurchaseService.VerifyAndCreditAsync's two steps).
//   Pass B asks Razorpay directly (via ICoinPackPurchaseService.ReconcileWithRazorpayAsync) whether
//     whatever's left actually got paid — this is the safety net for "client crashed AND the webhook
//     never arrived", which used to be silently marked abandoned despite money having been captured.
//   Pass C bulk-abandons only what Pass B positively confirmed as not paid (or gave up on after 24h).
// Each pass only ever operates on what the previous pass left unresolved, so a healable/reconcilable
// row is never mistakenly abandoned.
public class PendingCoinPurchaseCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PendingCoinPurchaseCleanupService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InconclusiveGiveUpAfter = TimeSpan.FromHours(24);

    public PendingCoinPurchaseCleanupService(IServiceScopeFactory serviceScopeFactory, ILogger<PendingCoinPurchaseCleanupService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PendingCoinPurchaseCleanupService starting, interval={IntervalMinutes}min", Interval.TotalMinutes);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RunSweepAsync();
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunSweepAsync()
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var purchaseService = scope.ServiceProvider.GetRequiredService<ICoinPackPurchaseService>();
            var walletNotifier = scope.ServiceProvider.GetRequiredService<IWalletNotifier>();
            var cutoff = DateTime.UtcNow - AbandonedAfter;
            var now = DateTime.UtcNow;

            var stuckPending = await db.CoinPackPurchases
                .Where(p => p.Status == CoinPackPurchaseStatuses.Pending && p.CreatedAt < cutoff)
                .ToListAsync();

            // Pass A — self-heal a purchase whose wallet credit already succeeded locally but whose
            // own Status flip never landed (crash between CoinWalletService.CreditCoinsAsync and
            // MarkSuccessIfPendingAsync in VerifyAndCreditAsync).
            var healedCount = 0;
            foreach (var purchase in stuckPending)
            {
                var alreadyCredited = await db.CoinTransactions.AnyAsync(t =>
                    t.Reason == CoinTransactionReasons.Recharge && t.ReferenceId == purchase.Id);
                if (alreadyCredited)
                {
                    purchase.Status = CoinPackPurchaseStatuses.Success;
                    purchase.CompletedAt = now;
                    healedCount++;
                }
            }
            if (healedCount > 0) await db.SaveChangesAsync();

            // Pass B — for whatever Pass A didn't resolve, ask Razorpay directly instead of assuming
            // abandonment. Sequential, not parallel: this only ever touches already-stuck edge cases
            // (most purchases complete via the client's own verify-call within seconds), so volume is
            // inherently tiny and doesn't warrant the complexity of a concurrent/batched call.
            var reconciledCount = 0;
            var stillInconclusiveCount = 0;
            var reconcileErrorCount = 0;
            var notPaidIds = new List<Guid>();
            var staleInconclusiveIds = new List<Guid>();
            foreach (var purchase in stuckPending.Where(p => p.Status == CoinPackPurchaseStatuses.Pending))
            {
                try
                {
                    var result = await purchaseService.ReconcileWithRazorpayAsync(purchase);
                    switch (result.Outcome)
                    {
                        case CoinPackReconcileOutcome.Credited:
                            reconciledCount++;
                            if (result.CreditResponse != null)
                            {
                                await walletNotifier.NotifyBalanceChangedAsync(
                                    purchase.UserId, result.CreditResponse.NewBalance, CoinTransactionReasons.Recharge);
                            }
                            break;
                        case CoinPackReconcileOutcome.NotPaid:
                            notPaidIds.Add(purchase.Id);
                            break;
                        case CoinPackReconcileOutcome.Inconclusive:
                            if (now - purchase.CreatedAt > InconclusiveGiveUpAfter)
                            {
                                staleInconclusiveIds.Add(purchase.Id);
                                _logger.LogError(
                                    "PendingCoinPurchaseCleanupService: purchase {PurchaseId} (order {OrderId}) still inconclusive with Razorpay after 24h — abandoning, needs manual review",
                                    purchase.Id, purchase.RazorpayOrderId);
                            }
                            else
                            {
                                stillInconclusiveCount++;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // One purchase's unexpected failure must never abort the rest of the sweep — it
                    // simply stays Pending and gets retried next cycle.
                    reconcileErrorCount++;
                    _logger.LogError(ex, "PendingCoinPurchaseCleanupService: reconciliation failed for purchase {PurchaseId}", purchase.Id);
                }
            }

            // Pass C — abandon only what Pass B positively confirmed as not paid, or gave up on after
            // 24h of inconclusive Razorpay responses. No longer a blanket "everything still pending".
            // Status == Pending is re-checked here (not just Id) because Pass B's classification and
            // this bulk update aren't atomic with each other — a webhook or the client's own
            // verify-call can credit and flip a purchase to Success in that window; without this guard
            // this update would silently stomp an already-paid purchase back to Abandoned.
            var notPaidCount = notPaidIds.Count == 0 ? 0 : await db.CoinPackPurchases
                .Where(p => notPaidIds.Contains(p.Id) && p.Status == CoinPackPurchaseStatuses.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, CoinPackPurchaseStatuses.Abandoned)
                    .SetProperty(p => p.FailureReason, "Reconciled with Razorpay — payment not completed")
                    .SetProperty(p => p.CompletedAt, now));

            var staleCount = staleInconclusiveIds.Count == 0 ? 0 : await db.CoinPackPurchases
                .Where(p => staleInconclusiveIds.Contains(p.Id) && p.Status == CoinPackPurchaseStatuses.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, CoinPackPurchaseStatuses.Abandoned)
                    .SetProperty(p => p.FailureReason, "Could not reconcile with Razorpay after 24h — manual review required")
                    .SetProperty(p => p.CompletedAt, now));

            var abandonedCount = notPaidCount + staleCount;
            // Logged whenever there was anything to look at, not just when something changed state —
            // a sweep where every stuck purchase comes back Inconclusive (e.g. Razorpay consistently
            // returning a response we can't parse) previously produced zero log output for up to 24h,
            // which defeats the point of a feature whose whole purpose is "don't lose money silently".
            if (stuckPending.Count > 0)
                _logger.LogInformation(
                    "PendingCoinPurchaseCleanupService: stuckPending={StuckPending}, healed={Healed}, reconciled={Reconciled}, abandoned={Abandoned}, stillInconclusive={StillInconclusive}, reconcileErrors={ReconcileErrors}",
                    stuckPending.Count, healedCount, reconciledCount, abandonedCount, stillInconclusiveCount, reconcileErrorCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in PendingCoinPurchaseCleanupService sweep");
        }
    }
}
