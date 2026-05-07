using System.Security.Claims;
using Mapster;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class UsersHandlers
{
    public static async Task<IResult> GetProfile(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        return OkResponse(user.Adapt<UserDto>());
    }

    public static async Task<IResult> UpdateProfile(
        UpdateProfileRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork)
    {
        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        if (request.Name != null) user.Name = request.Name;
        if (request.GmailId != null) user.GmailId = request.GmailId;
        user.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Users.UpdateAsync(user);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(user.Adapt<UserDto>());
    }

    internal static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = Guid.Empty;
        var value = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return value != null && Guid.TryParse(value, out userId);
    }
}
