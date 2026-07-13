using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

public class PendingPaymentCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PendingPaymentCleanupService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    // Comfortably past the 20-minute expire_by now set on every Razorpay order
    // (RazorpayService.CreateOrderAsync) — by this point Razorpay itself has already refused
    // to accept a payment against the order, so a still-PENDING transaction is genuinely
    // abandoned, not just presumed to be.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(30);

    public PendingPaymentCleanupService(
        IServiceScopeFactory serviceScopeFactory, ILogger<PendingPaymentCleanupService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PendingPaymentCleanupService starting, interval={IntervalMinutes}min", Interval.TotalMinutes);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await CleanupAbandonedTransactionsAsync();
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAbandonedTransactionsAsync()
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var cutoff = DateTime.UtcNow - AbandonedAfter;
            var now = DateTime.UtcNow;

            // Single atomic UPDATE ... WHERE Status = 'PENDING' ... — the WHERE clause is
            // evaluated by the database at the exact moment this statement runs, not from a
            // stale in-memory read. If the client's own /verify-payment call or the Razorpay
            // webhook flips a row to SUCCESS/FAILED a moment before this executes, that row no
            // longer matches Status == "PENDING" and this UPDATE silently skips it.
            //
            // Deliberately writes "ABANDONED", NOT "FAILED": a real payment can genuinely take
            // longer than AbandonedAfter to confirm (slow bank OTP, a delayed Razorpay webhook
            // retry, a user switching apps mid-checkout) — if this job marked such a row
            // "FAILED", every recovery path (VerifyAndActivateAsync's own "Status == FAILED"
            // guard, and RazorpayWebhook's "Status != PENDING" no-op guard) would permanently
            // refuse to activate it even once the real payment.captured event arrived later —
            // silently losing a plan the user genuinely paid for, worse than the original
            // stuck-PENDING clutter this job exists to clean up. "ABANDONED" is a distinct,
            // non-terminal status: it doesn't match either guard's exact "SUCCESS"/"FAILED"
            // check, so a late-arriving legitimate webhook or verify call still activates it
            // normally, while "FAILED" (a real signature-verification rejection) remains
            // correctly terminal.
            var affected = await db.PaymentTransactions
                .Where(t => t.Status == "PENDING" && t.CreatedAt < cutoff)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, "ABANDONED")
                    .SetProperty(t => t.FailureReason, "Abandoned — payment never completed within the time window")
                    .SetProperty(t => t.CompletedAt, now));

            if (affected > 0)
                _logger.LogInformation("PendingPaymentCleanupService: marked {Count} abandoned transaction(s) as ABANDONED", affected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in PendingPaymentCleanupService cleanup pass");
        }
    }
}
