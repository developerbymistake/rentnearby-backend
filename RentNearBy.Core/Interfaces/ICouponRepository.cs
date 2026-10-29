using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICouponRepository : IRepository<Coupon>
{
    Task<Coupon?> GetByCodeAsync(string normalizedCode);
    Task<bool> CodeExistsAsync(string normalizedCode, Guid? exceptId);
    Task<(IReadOnlyList<Coupon> Items, int TotalCount)> GetPagedAsync(string? status, int page, int pageSize);
    // Status-guarded: only an Active coupon is revoked.
    Task<bool> RevokeAsync(Guid id);
}
