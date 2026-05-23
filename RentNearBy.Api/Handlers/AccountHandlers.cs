using System.Security.Claims;
using RentNearBy.Api.Extensions;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Api.Handlers;

public static class AccountHandlers
{
    public static async Task<IResult> DeleteAccount(
        ClaimsPrincipal principal,
        IAccountDeletionService deletionService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return ApiResults.UnauthorizedResponse();

        await deletionService.DeleteAccountAsync(userId);
        return ApiResults.NoContentResponse();
    }
}
