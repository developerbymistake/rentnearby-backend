using System.Security.Claims;
using RentNearBy.Api.Extensions;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services.Owner.Quote;

namespace RentNearBy.Api.Handlers;

public static class AccountHandlers
{
    public static async Task<IResult> DeleteAccount(
        ClaimsPrincipal principal,
        IAccountDeletionService deletionService,
        [Microsoft.AspNetCore.Mvc.FromServices] SubscriptionDeletionPreCheck? subscriptionPreCheck = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return ApiResults.UnauthorizedResponse();

        // Google is asked about an auto-renewing subscription here, outside the deletion transaction (ARCHITECTURE 11.3).
        if (subscriptionPreCheck != null) await subscriptionPreCheck.RefreshAsync(userId);

        var result = await deletionService.DeleteAccountAsync(userId);
        if (!result.Deleted)
            return ApiResults.ErrorResponse(409, result.Reasons[0].Message, AccountDeletionRules.ErrorType,
                new { reasons = result.Reasons });

        return ApiResults.NoContentResponse();
    }
}
