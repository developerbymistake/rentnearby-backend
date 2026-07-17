using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICouponRepository : IRepository<Coupon>
{
    Task<Coupon?> GetByCodeAsync(string normalizedCode);

    // One atomic statement: guard (Active, within validity window, under MaxTotalRedemptions) +
    // increment CurrentRedemptions + flip Status to Exhausted if this redemption fills the last
    // slot — all in the same UPDATE, so two simultaneous redemption attempts on the last remaining
    // slot can't both succeed.
    Task<bool> TryReserveRedemptionSlotAsync(Guid couponId, DateTime now);

    Task<(IReadOnlyList<Coupon> Items, int TotalCount)> GetPagedAsync(string? status, string? triggerType, string? search, int page, int pageSize);

    // Needed by the admin code-collision retry loop (Phase 6): a candidate Coupon that failed to
    // save due to a duplicate Code must be detached before regenerating and retrying, or EF Core's
    // change tracker still has it tracked under the old (now-abandoned) Code value.
    void DetachTracked(Coupon coupon);
}
