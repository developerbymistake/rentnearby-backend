using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CoinPackPurchaseRepository(ApplicationDbContext context) : ICoinPackPurchaseRepository
{
    public async Task AddAsync(CoinPackPurchase purchase)
        => await context.CoinPackPurchases.AddAsync(purchase);

    public async Task<CoinPackPurchase?> GetByIdAsync(Guid id)
        => await context.CoinPackPurchases.FirstOrDefaultAsync(p => p.Id == id);

    public async Task<CoinPackPurchase?> GetByRazorpayOrderIdAsync(string orderId)
        => await context.CoinPackPurchases.FirstOrDefaultAsync(p => p.RazorpayOrderId == orderId);

    public async Task<IEnumerable<CoinPackPurchase>> GetByUserIdAsync(Guid userId)
        => await context.CoinPackPurchases
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

    public async Task<bool> MarkSuccessIfPendingAsync(Guid purchaseId, string paymentId, string signature)
    {
        var now = DateTime.UtcNow;
        // Matches PENDING/ABANDONED/CANCELLED/FAILED, not just PENDING — a purchase can legitimately
        // reach this call after already being swept to ABANDONED (PendingCoinPurchaseCleanupService, a
        // late webhook/reconciliation arriving past the 30-min window), CANCELLED (the user's own
        // /cancel-order call racing a webhook), or FAILED (a late authorisation after an apparent
        // failure — common with UPI — where an earlier attempt on the same order failed but a retry
        // captured; PaymentHandlers.RazorpayWebhook already documents this exact case as one that must
        // still credit). VerifyAndCreditAsync's wallet credit is idempotent either way, but without
        // this the purchase row itself stayed permanently stuck showing failed/cancelled despite the
        // coins having actually landed — exactly the kind of state a support agent could misread and
        // double-credit manually. SUCCESS is excluded: never re-flip an already-settled success.
        var affected = await context.CoinPackPurchases
            .Where(p => p.Id == purchaseId && (
                p.Status == CoinPackPurchaseStatuses.Pending ||
                p.Status == CoinPackPurchaseStatuses.Abandoned ||
                p.Status == CoinPackPurchaseStatuses.Cancelled ||
                p.Status == CoinPackPurchaseStatuses.Failed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CoinPackPurchaseStatuses.Success)
                .SetProperty(p => p.RazorpayPaymentId, paymentId)
                .SetProperty(p => p.RazorpaySignature, p => string.IsNullOrEmpty(signature) ? p.RazorpaySignature : signature)
                .SetProperty(p => p.CompletedAt, now));

        return affected > 0;
    }

    public async Task<bool> MarkAbandonedIfPendingAsync(Guid purchaseId, string reason)
    {
        var affected = await context.CoinPackPurchases
            .Where(p => p.Id == purchaseId && p.Status == CoinPackPurchaseStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CoinPackPurchaseStatuses.Abandoned)
                .SetProperty(p => p.FailureReason, reason)
                .SetProperty(p => p.CompletedAt, DateTime.UtcNow));

        return affected > 0;
    }

    public async Task<bool> MarkCancelledIfPendingAsync(Guid purchaseId)
    {
        var affected = await context.CoinPackPurchases
            .Where(p => p.Id == purchaseId && p.Status == CoinPackPurchaseStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CoinPackPurchaseStatuses.Cancelled)
                .SetProperty(p => p.CompletedAt, DateTime.UtcNow));

        return affected > 0;
    }

    public async Task<bool> MarkFailedIfPendingOrAbandonedAsync(Guid purchaseId, string paymentId, string failureReason)
    {
        var affected = await context.CoinPackPurchases
            .Where(p => p.Id == purchaseId && (
                p.Status == CoinPackPurchaseStatuses.Pending ||
                p.Status == CoinPackPurchaseStatuses.Abandoned))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CoinPackPurchaseStatuses.Failed)
                .SetProperty(p => p.FailureReason, failureReason)
                .SetProperty(p => p.RazorpayPaymentId, paymentId)
                .SetProperty(p => p.CompletedAt, DateTime.UtcNow));

        return affected > 0;
    }
}
