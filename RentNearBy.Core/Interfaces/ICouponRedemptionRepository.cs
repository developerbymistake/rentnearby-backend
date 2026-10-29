using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public sealed record CouponRedemptionRow(
    Guid Id, Guid? UserId, string? UserPhone, Guid PurchaseId, string? PlanName, int DiscountInr, string Status, DateTime CreatedAt);

public interface ICouponRedemptionRepository
{
    Task<(IReadOnlyList<CouponRedemptionRow> Items, bool HasMore)> GetByCouponPagedAsync(Guid couponId, int page, int pageSize);
}
