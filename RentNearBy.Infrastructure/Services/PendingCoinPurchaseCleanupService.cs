using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Standalone sweep for CoinPackPurchase — does NOT extend PendingPaymentCleanupService (which swept
// the now-deleted PaymentTransaction table). Two passes per run: Pass A self-heals a purchase whose
// wallet credit already succeeded but whose own Status flip never landed (a crash between
// CoinPackPurchaseService.VerifyAndCreditAsync's two steps); Pass B marks whatever's left genuinely
// abandoned. Pass A always runs before Pass B so a healable row is never mistakenly abandoned.
public class PendingCoinPurchaseCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PendingCoinPurchaseCleanupService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(30);

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
            var cutoff = DateTime.UtcNow - AbandonedAfter;
            var now = DateTime.UtcNow;

            var stuckPending = await db.CoinPackPurchases
                .Where(p => p.Status == CoinPackPurchaseStatuses.Pending && p.CreatedAt < cutoff)
                .ToListAsync();

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

            var abandonedCount = await db.CoinPackPurchases
                .Where(p => p.Status == CoinPackPurchaseStatuses.Pending && p.CreatedAt < cutoff)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, CoinPackPurchaseStatuses.Abandoned)
                    .SetProperty(p => p.FailureReason, "Abandoned — payment never completed within the time window")
                    .SetProperty(p => p.CompletedAt, now));

            if (healedCount > 0 || abandonedCount > 0)
                _logger.LogInformation("PendingCoinPurchaseCleanupService: healed={Healed}, abandoned={Abandoned}", healedCount, abandonedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in PendingCoinPurchaseCleanupService sweep");
        }
    }
}
