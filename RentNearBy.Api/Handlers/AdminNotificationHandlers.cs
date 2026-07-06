using System.Security.Claims;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Interfaces;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AdminNotificationHandlers
{
    public static async Task<IResult> RegisterToken(
        RegisterDeviceTokenRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return BadRequestResponse("Token is required");

        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId))
            return UnauthorizedResponse();

        await unitOfWork.AdminDeviceTokens.UpsertAsync(adminId, request.Token);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(new { message = "Token registered" });
    }

    public static async Task<IResult> UnregisterToken(
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork)
    {
        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId))
            return UnauthorizedResponse();

        await unitOfWork.AdminDeviceTokens.DeleteByAdminIdAsync(adminId);
        return OkResponse(new { message = "Tokens cleared" });
    }
}
