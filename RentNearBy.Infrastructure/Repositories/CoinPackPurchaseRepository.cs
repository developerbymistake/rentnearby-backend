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
        var affected = await context.CoinPackPurchases
            .Where(p => p.Id == purchaseId && p.Status == CoinPackPurchaseStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CoinPackPurchaseStatuses.Success)
                .SetProperty(p => p.RazorpayPaymentId, paymentId)
                .SetProperty(p => p.RazorpaySignature, signature)
                .SetProperty(p => p.CompletedAt, now));

        return affected > 0;
    }
}
