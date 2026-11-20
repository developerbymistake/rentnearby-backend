using System.Security.Claims;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Buddy.Be4;

// N1, N2: the feature's stored notices (UserNotices). Limit notice:read 120 per minute.
public static class NoticeHandlers
{
    public static async Task<IResult> List(
        ClaimsPrincipal principal, NoticeReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? cursor = null, int? limit = null)
    {
        if (!BuddyHttp.TryCaller(principal, out var userId)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.NoticeRead(userId)) is { } limited) return limited;
        if (!BuddyHttp.TryKeyset(cursor, out var after)) return BuddyHttp.BadCursor();
        var take = BuddyHttp.ClampLimit(limit, BuddyLimits.NoticesPageDefault, BuddyLimits.NoticesPageMax);
        return OkResponse(await reader.PageAsync(userId, after, take, ct));
    }

    // Always the same answer: no existence oracle.
    public static async Task<IResult> MarkRead(
        Guid id, ClaimsPrincipal principal, NoticeReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var userId)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.NoticeRead(userId)) is { } limited) return limited;
        await reader.MarkReadAsync(id, userId, ct);
        return OkResponse(new { message = "Marked read" });
    }
}
