using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CouponRepository(ApplicationDbContext context) : Repository<Coupon>(context), ICouponRepository
{
    public async Task<Coupon?> GetByCodeAsync(string normalizedCode)
        => await _dbSet.AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalizedCode);

    public async Task<bool> CodeExistsAsync(string normalizedCode, Guid? exceptId)
        => await _dbSet.AnyAsync(c => c.Code == normalizedCode && (exceptId == null || c.Id != exceptId));

    public async Task<(IReadOnlyList<Coupon> Items, int TotalCount)> GetPagedAsync(string? status, int page, int pageSize)
    {
        var query = _dbSet.AsNoTracking();
        if (!string.IsNullOrEmpty(status)) query = query.Where(c => c.Status == status);
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items, total);
    }

    public async Task<bool> RevokeAsync(Guid id)
        => await _dbSet
            .Where(c => c.Id == id && c.Status != CouponStatuses.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CouponStatuses.Revoked)
                .SetProperty(c => c.AutoApply, false)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow)) > 0;
}
