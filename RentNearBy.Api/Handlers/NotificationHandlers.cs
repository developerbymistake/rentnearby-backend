using System.Security.Claims;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Interfaces;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class NotificationHandlers
{
    public static async Task<IResult> RegisterToken(
        RegisterDeviceTokenRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return BadRequestResponse("Token is required");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        await unitOfWork.DeviceTokens.UpsertAsync(userId, request.Token);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(new { message = "Token registered" });
    }

    public static async Task<IResult> UnregisterToken(
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        await unitOfWork.DeviceTokens.DeleteByUserIdAsync(userId);
        return OkResponse(new { message = "Tokens cleared" });
    }
}
