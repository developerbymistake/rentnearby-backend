using System.Security.Claims;
using Mapster;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AdminNotificationHandlers
{
    // System-wide feed of every NotificationEvent (today, exclusively Agent lead-assignments) — no
    // per-admin scoping, no new storage, just the existing table read without the TargetUserId
    // filter GetMyNotifications uses. See INotificationRepository.GetPagedForAdminAsync.
    public static async Task<IResult> AdminGetNotifications(
        IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (pageSize < 1 || pageSize > 50) pageSize = 20;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Notifications.GetPagedForAdminAsync(page, pageSize);
        var dtos = items.Select(i =>
        {
            var dto = i.Notification.Adapt<AdminNotificationDto>();
            dto.TargetAgentName = i.TargetAgentName;
            return dto;
        });
        return OkResponse(new { items = dtos, hasMore });
    }

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
