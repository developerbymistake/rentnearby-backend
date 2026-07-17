using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CouponRepository(ApplicationDbContext context) : Repository<Coupon>(context), ICouponRepository
{
    public async Task<Coupon?> GetByCodeAsync(string normalizedCode)
        => await context.Coupons.FirstOrDefaultAsync(c => c.Code == normalizedCode);

    public async Task<bool> TryReserveRedemptionSlotAsync(Guid couponId, DateTime now)
    {
        var affected = await context.Coupons
            .Where(c => c.Id == couponId
                && c.Status == CouponStatuses.Active
                && c.ValidFrom <= now
                && (c.ValidUntil == null || c.ValidUntil > now)
                && (c.MaxTotalRedemptions == null || c.CurrentRedemptions < c.MaxTotalRedemptions.Value))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.CurrentRedemptions, c => c.CurrentRedemptions + 1)
                .SetProperty(c => c.Status, c =>
                    c.MaxTotalRedemptions != null && c.CurrentRedemptions + 1 >= c.MaxTotalRedemptions.Value
                        ? CouponStatuses.Exhausted
                        : c.Status)
                .SetProperty(c => c.UpdatedAt, now));

        return affected > 0;
    }

    public async Task<(IReadOnlyList<Coupon> Items, int TotalCount)> GetPagedAsync(string? status, string? triggerType, string? search, int page, int pageSize)
    {
        var query = context.Coupons.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(triggerType)) query = query.Where(c => c.TriggerType == triggerType);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(c => (c.Code != null && c.Code.Contains(search)) || (c.CampaignLabel != null && c.CampaignLabel.Contains(search)));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, total);
    }

    public void DetachTracked(Coupon coupon)
        => context.Entry(coupon).State = EntityState.Detached;
}
