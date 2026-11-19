using System.Security.Claims;
using RentNearBy.Api.Extensions;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Handlers;

public static class AccountHandlers
{
    public static async Task<IResult> DeleteAccount(
        ClaimsPrincipal principal,
        IAccountDeletionService deletionService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return ApiResults.UnauthorizedResponse();

        var result = await deletionService.DeleteAccountAsync(userId);
        if (!result.Deleted)
            return ApiResults.ErrorResponse(409, result.Reasons[0].Message, AccountDeletionRules.ErrorType,
                new { reasons = result.Reasons });

        return ApiResults.NoContentResponse();
    }
}
