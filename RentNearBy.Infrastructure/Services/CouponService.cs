using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public class CouponService(IUnitOfWork unitOfWork, ICoinWalletService wallet, ILogger<CouponService> logger) : ICouponService
{
    public async Task<CouponRedeemResult> RedeemCouponByCodeAsync(Guid userId, string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var coupon = await unitOfWork.Coupons.GetByCodeAsync(normalized);
        return coupon == null
            ? new CouponRedeemResult(CouponRedeemOutcome.NotFound)
            : await RedeemAsync(userId, coupon);
    }

    public async Task<CouponRedeemResult> RedeemCouponAsync(Guid userId, Guid couponId)
    {
        var coupon = await unitOfWork.Coupons.GetByIdAsync(couponId);
        return coupon == null
            ? new CouponRedeemResult(CouponRedeemOutcome.NotFound)
            : await RedeemAsync(userId, coupon);
    }

    private async Task<CouponRedeemResult> RedeemAsync(Guid userId, Coupon coupon)
    {
        var now = DateTime.UtcNow;
        if (coupon.Status == CouponStatuses.Revoked) return new CouponRedeemResult(CouponRedeemOutcome.Revoked);
        if (coupon.Status == CouponStatuses.Exhausted) return new CouponRedeemResult(CouponRedeemOutcome.Exhausted);
        if (coupon.ValidFrom > now) return new CouponRedeemResult(CouponRedeemOutcome.NotYetValid);
        if (coupon.ValidUntil.HasValue && coupon.ValidUntil <= now) return new CouponRedeemResult(CouponRedeemOutcome.Expired);

        var redemptionId = Guid.NewGuid();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            var reserved = await unitOfWork.Coupons.TryReserveRedemptionSlotAsync(coupon.Id, now);
            if (!reserved)
            {
                await unitOfWork.RollbackTransactionAsync();
                return new CouponRedeemResult(CouponRedeemOutcome.Exhausted);
            }

            await unitOfWork.CouponRedemptions.AddAsync(new CouponRedemption
            {
                Id = redemptionId,
                CouponId = coupon.Id,
                UserId = userId,
                RedeemedAt = now,
            });

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // (CouponId, UserId) unique-index violation — this user already redeemed this coupon.
                await unitOfWork.RollbackTransactionAsync();
                logger.LogInformation("RedeemAsync: user {UserId} already redeemed coupon {CouponId}", userId, coupon.Id);
                return new CouponRedeemResult(CouponRedeemOutcome.AlreadyRedeemed);
            }

            // Credited inside the same transaction as the redemption row — a crash between the two
            // used to be unrecoverable (the unique index blocks any retry from ever reaching the
            // credit once the redemption row exists), permanently stranding the user's coins. Sharing
            // the transaction with CoinWalletService works the same way GoLiveHandlers' spend+listing-
            // update does: ICoinWalletService detects this ambient transaction (same ApplicationDbContext)
            // and uses a SAVEPOINT instead of opening its own, so a failure here still rolls back the
            // redemption row too — no more split-brain state.
            //
            // ReferenceId is the fresh redemption row's own Guid, not the coupon's Id — every redeemer
            // of a shared/multi-user coupon shares the same CouponId, so keying the wallet engine's own
            // one-shot-credit dedup off CouponId would silently credit only the first redeemer and
            // treat everyone else as a duplicate.
            var reason = coupon.TriggerType == WellKnownCoupons.WelcomeSignupTrigger
                ? CoinTransactionReasons.WelcomeBonus
                : CoinTransactionReasons.CouponRedeem;
            var creditResult = await wallet.CreditCoinsAsync(userId, coupon.CoinValue, reason, redemptionId);

            await unitOfWork.CommitTransactionAsync();

            return new CouponRedeemResult(CouponRedeemOutcome.Success, coupon.CoinValue, creditResult.BalanceAfter, coupon.CampaignLabel);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }
}
