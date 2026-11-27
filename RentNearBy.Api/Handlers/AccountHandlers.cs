using System.Security.Claims;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services.Owner.Quote;

namespace RentNearBy.Api.Handlers;

public static class AccountHandlers
{
    public static async Task<IResult> DeleteAccount(
        ClaimsPrincipal principal,
        IAccountDeletionService deletionService,
        IRateLimitService limiter,
        HttpContext http,
        [Microsoft.AspNetCore.Mvc.FromServices] SubscriptionDeletionPreCheck? subscriptionPreCheck = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return ApiResults.UnauthorizedResponse();

        // Own bucket before the Google pre-check (each call may wait up to 5 s on Google).
        var limit = AccountRateLimits.Delete(userId);
        var check = await limiter.CheckAsync(limit.Key, limit.Max, limit.Window);
        if (!check.IsAllowed) return OwnerHttp.Denied(http, check, limit.Window);

        // Google is asked about an auto-renewing subscription here, outside the deletion transaction (ARCHITECTURE 11.3).
        if (subscriptionPreCheck != null) await subscriptionPreCheck.RefreshAsync(userId);

        var result = await deletionService.DeleteAccountAsync(userId);
        if (!result.Deleted)
            return ApiResults.ErrorResponse(409, result.Reasons[0].Message, AccountDeletionRules.ErrorType,
                new { reasons = result.Reasons });

        return ApiResults.NoContentResponse();
    }
}
