using System.Security.Claims;
using Mapster;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Consumer-facing notification inbox — separate from NotificationHandlers.cs, which only owns
// FCM device-token register/unregister. Every method here resolves the caller from their own JWT
// via UsersHandlers.TryGetUserId — never a client-supplied user id.
public static class NotificationInboxHandlers
{
    public static async Task<IResult> GetMyNotifications(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20, string? cursor = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        if (pageSize < 1 || pageSize > 50) pageSize = 20;
        if (page < 1) page = 1;

        // cursor = keyset (new apps); page > 1 without a cursor = the old app's page paging.
        DateTime? afterAt = null;
        Guid? afterId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!KeysetCursor.TryDecode(cursor, out var at, out var id)) return BadRequestResponse("Invalid cursor");
            afterAt = at;
            afterId = id;
        }
        var (items, hasMore) = afterAt == null && page > 1
            ? await unitOfWork.Notifications.GetPagedForUserAsync(userId, page, pageSize)
            : await unitOfWork.Notifications.GetForUserAfterCursorAsync(userId, afterAt, afterId, pageSize);
        var nextCursor = KeysetCursor.EncodeLast(items, hasMore, i => i.Notification.CreatedAt, i => i.Notification.Id);
        var dtos = items.Select(i =>
        {
            var dto = i.Notification.Adapt<NotificationDto>();
            dto.IsRead = i.IsRead;
            return dto;
        });
        return OkResponse(new { items = dtos, hasMore, nextCursor });
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
