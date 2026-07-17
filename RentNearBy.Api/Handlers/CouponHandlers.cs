using System.Security.Claims;
using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class CouponHandlers
{
    public static async Task<IResult> RedeemCoupon(
        RedeemCouponRequest request,
        IValidator<RedeemCouponRequest> validator,
        ClaimsPrincipal principal,
        ICouponService couponService,
        IRateLimitService rateLimiter)
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
        return result.Outcome switch
        {
            CouponRedeemOutcome.Success => OkResponse(new CouponRedeemResponseDto
            {
                CoinsCredited = result.CoinsCredited,
                NewBalance = result.NewBalance ?? 0,
                CampaignLabel = result.CampaignLabel,
            }),
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
