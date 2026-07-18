using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class CouponHandlers
{
    public static async Task<IResult> RedeemCoupon(
        RedeemCouponRequest request,
        IValidator<RedeemCouponRequest> validator,
        ClaimsPrincipal principal,
        ICouponService couponService,
        IRateLimitService rateLimiter,
        IHubContext<WalletHub> hubContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"coupon:redeem:{userId}", maxAttempts: 5, window: TimeSpan.FromHours(1));
        if (!rl.IsAllowed)
            return TooManyRequestsResponse();

        var result = await couponService.RedeemCouponByCodeAsync(userId, request.Code);

        // Success is pulled out of the switch (rather than an arm) because it's the one outcome that
        // needs to push a WalletBalanceChanged event before returning — a switch expression arm can't
        // do that inline. Push failures are swallowed — never let a SignalR hiccup turn an
        // already-credited redemption into an error response.
        if (result.Outcome == CouponRedeemOutcome.Success)
        {
            var newBalance = result.NewBalance ?? 0;
            try
            {
                await hubContext.Clients.Group($"user_{userId}").SendAsync("WalletBalanceChanged", new
                {
                    balance = newBalance,
                    reason = CoinTransactionReasons.CouponRedeem,
                    occurredAt = DateTime.UtcNow,
                });
            }
            catch { }

            return OkResponse(new CouponRedeemResponseDto
            {
                CoinsCredited = result.CoinsCredited,
                NewBalance = newBalance,
                CampaignLabel = result.CampaignLabel,
            });
        }

        return result.Outcome switch
        {
            CouponRedeemOutcome.AlreadyRedeemed => ConflictResponse("You have already redeemed this code.", "ALREADY_REDEEMED"),
            CouponRedeemOutcome.NotFound => NotFoundResponse("Invalid code."),
            CouponRedeemOutcome.Expired => BadRequestResponse("This code has expired."),
            CouponRedeemOutcome.NotYetValid => BadRequestResponse("This code is not active yet."),
            CouponRedeemOutcome.Exhausted => BadRequestResponse("This code has reached its redemption limit."),
            CouponRedeemOutcome.Revoked => BadRequestResponse("This code is no longer valid."),
            _ => ServerErrorResponse(),
        };
    }
}
