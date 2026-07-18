namespace RentNearBy.Core.Interfaces;

public enum CouponRedeemOutcome { Success, AlreadyRedeemed, NotFound, Expired, NotYetValid, Exhausted, Revoked }

public record CouponRedeemResult(CouponRedeemOutcome Outcome, int CoinsCredited = 0, int? NewBalance = null, string? CampaignLabel = null);

public interface ICouponService
{
    Task<CouponRedeemResult> RedeemCouponByCodeAsync(Guid userId, string code);
    Task<CouponRedeemResult> RedeemCouponAsync(Guid userId, Guid couponId);
}
