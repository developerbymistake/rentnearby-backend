using System.Security.Claims;
using Mapster;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Consumer-facing notification inbox — separate from NotificationHandlers.cs, which only owns
// FCM device-token register/unregister. Every method here resolves the caller from their own JWT
// via UsersHandlers.TryGetUserId — never a client-supplied user id.
public static class NotificationInboxHandlers
{
    public static async Task<IResult> GetMyNotifications(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        if (pageSize < 1 || pageSize > 50) pageSize = 20;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Notifications.GetPagedForUserAsync(userId, page, pageSize);
        var dtos = items.Select(i =>
        {
            var dto = i.Notification.Adapt<NotificationDto>();
            dto.IsRead = i.IsRead;
            return dto;
        });
        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> GetUnreadCount(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var count = await unitOfWork.Notifications.GetUnreadCountAsync(userId);
        return OkResponse(new { count });
    }

    // Always OkResponse regardless of rows-affected — a wrong/foreign/nonexistent id is
    // indistinguishable from an already-read one, so the endpoint can never leak whether a given
    // notification id exists or belongs to someone else.
    public static async Task<IResult> MarkNotificationRead(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        await unitOfWork.Notifications.MarkReadAsync(id, userId);
        return OkResponse(new { message = "Marked read" });
    }

    public static async Task<IResult> MarkAllNotificationsRead(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        await unitOfWork.Notifications.MarkAllReadAsync(userId);
        return OkResponse(new { message = "All marked read" });
    }
}
