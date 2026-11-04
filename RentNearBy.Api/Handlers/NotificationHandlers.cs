using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
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

    // Removes only the calling device's token (query ?token= or JSON body {token}); a caller that
    // sends no token (older app builds) keeps the old clear-all behaviour.
    public static async Task<IResult> UnregisterToken(
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        HttpRequest httpRequest,
        [FromQuery] string? token = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        if (string.IsNullOrWhiteSpace(token) && httpRequest.HasJsonContentType())
        {
            try
            {
                var body = await httpRequest.ReadFromJsonAsync<RegisterDeviceTokenRequest>();
                token = body?.Token;
            }
            catch (JsonException) { }
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            await unitOfWork.DeviceTokens.DeleteForUserAsync(userId, token.Trim());
            return OkResponse(new { message = "Token cleared" });
        }

        await unitOfWork.DeviceTokens.DeleteByUserIdAsync(userId);
        return OkResponse(new { message = "Tokens cleared" });
    }
}
