using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CouponRedemptionRepository(ApplicationDbContext context) : ICouponRedemptionRepository
{
    public async Task<(IReadOnlyList<CouponRedemptionRow> Items, bool HasMore)> GetByCouponPagedAsync(
        Guid couponId, int page, int pageSize)
    {
        var rows = await (
            from r in context.CouponRedemptions.AsNoTracking()
            where r.CouponId == couponId
            join p in context.PlanPurchases on r.PurchaseId equals p.Id
            join u in context.Users on r.UserId equals (Guid?)u.Id into users
            from u in users.DefaultIfEmpty()
            orderby r.CreatedAt descending, r.Id descending
            select new CouponRedemptionRow(
                r.Id, r.UserId, u != null ? u.PhoneNumber : null, r.PurchaseId, p.PlanNameSnapshot,
                r.DiscountInr, r.Status, r.CreatedAt))
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync();

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return (rows, hasMore);
    }
}
