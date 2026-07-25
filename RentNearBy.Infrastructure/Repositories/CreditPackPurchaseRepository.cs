using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CreditPackPurchaseRepository(ApplicationDbContext context) : ICreditPackPurchaseRepository
{
    public async Task AddAsync(CreditPackPurchase purchase)
        => await context.CreditPackPurchases.AddAsync(purchase);

    public async Task<CreditPackPurchase?> GetByIdAsync(Guid id)
        => await context.CreditPackPurchases.FirstOrDefaultAsync(p => p.Id == id);

    public async Task<CreditPackPurchase?> GetByRazorpayOrderIdAsync(string orderId)
        => await context.CreditPackPurchases.FirstOrDefaultAsync(p => p.RazorpayOrderId == orderId);

    public async Task<IEnumerable<CreditPackPurchase>> GetByUserIdAsync(Guid userId)
        => await context.CreditPackPurchases
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

    public async Task<bool> MarkSuccessIfPendingAsync(Guid purchaseId, string paymentId, string signature)
    {
        var now = DateTime.UtcNow;
        // Matches PENDING/ABANDONED/CANCELLED/FAILED, not just PENDING — a purchase can legitimately
        // reach this call after already being swept to ABANDONED (PendingCreditPurchaseCleanupService, a
        // late webhook/reconciliation arriving past the 30-min window), CANCELLED (the user's own
        // /cancel-order call racing a webhook), or FAILED (a late authorisation after an apparent
        // failure — common with UPI — where an earlier attempt on the same order failed but a retry
        // captured; PaymentHandlers.RazorpayWebhook already documents this exact case as one that must
        // still credit). VerifyAndCreditAsync's wallet credit is idempotent either way, but without
        // this the purchase row itself stayed permanently stuck showing failed/cancelled despite the
        // credits having actually landed — exactly the kind of state a support agent could misread and
        // double-credit manually. SUCCESS is excluded: never re-flip an already-settled success.
        var affected = await context.CreditPackPurchases
            .Where(p => p.Id == purchaseId && (
                p.Status == CreditPackPurchaseStatuses.Pending ||
                p.Status == CreditPackPurchaseStatuses.Abandoned ||
                p.Status == CreditPackPurchaseStatuses.Cancelled ||
                p.Status == CreditPackPurchaseStatuses.Failed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CreditPackPurchaseStatuses.Success)
                .SetProperty(p => p.RazorpayPaymentId, paymentId)
                .SetProperty(p => p.RazorpaySignature, p => string.IsNullOrEmpty(signature) ? p.RazorpaySignature : signature)
                .SetProperty(p => p.CompletedAt, now));

        return affected > 0;
    }

    public async Task<bool> MarkAbandonedIfPendingAsync(Guid purchaseId, string reason)
    {
        var affected = await context.CreditPackPurchases
            .Where(p => p.Id == purchaseId && p.Status == CreditPackPurchaseStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CreditPackPurchaseStatuses.Abandoned)
                .SetProperty(p => p.FailureReason, reason)
                .SetProperty(p => p.CompletedAt, DateTime.UtcNow));

        return affected > 0;
    }

    public async Task<bool> MarkCancelledIfPendingAsync(Guid purchaseId)
    {
        var affected = await context.CreditPackPurchases
            .Where(p => p.Id == purchaseId && p.Status == CreditPackPurchaseStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CreditPackPurchaseStatuses.Cancelled)
                .SetProperty(p => p.CompletedAt, DateTime.UtcNow));

        return affected > 0;
    }

    public async Task<bool> MarkFailedIfPendingOrAbandonedAsync(Guid purchaseId, string paymentId, string failureReason)
    {
        var affected = await context.CreditPackPurchases
            .Where(p => p.Id == purchaseId && (
                p.Status == CreditPackPurchaseStatuses.Pending ||
                p.Status == CreditPackPurchaseStatuses.Abandoned))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CreditPackPurchaseStatuses.Failed)
                .SetProperty(p => p.FailureReason, failureReason)
                .SetProperty(p => p.RazorpayPaymentId, paymentId)
                .SetProperty(p => p.CompletedAt, DateTime.UtcNow));

        return affected > 0;
    }
}
