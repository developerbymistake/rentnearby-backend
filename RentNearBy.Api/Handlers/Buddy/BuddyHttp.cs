using System.Security.Claims;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Buddy;

// Shared HTTP helpers of the buddy feature (pattern TaxiRateLimits): caller id, rate limits, typed errors, cursors.
public static class BuddyHttp
{
    public static bool TryCaller(ClaimsPrincipal principal, out Guid userId) => UsersHandlers.TryGetUserId(principal, out userId);

    // Reads: one permit, never given back. null = allowed.
    public static async Task<IResult?> ReadLimitAsync(IRateLimitService limiter, HttpContext http, BuddyRateLimit limit)
    {
        var result = await limiter.CheckAsync(limit.Key, limit.Max, limit.Window);
        return result.IsAllowed ? null : Denied(http, result, limit.Window);
    }

    // Actions: permits are given back when the action did no work (non-2xx or an exception), unless keepOnFailure.
    public static async Task<IResult> ActionLimitAsync(
        IRateLimitService limiter, HttpContext http, BuddyRateLimit limit, Func<Task<IResult>> action, int permits = 1, bool keepOnFailure = false)
    {
        var lease = new RateLimitPermits(limiter);
        var taken = await lease.TakeAsync(limit.Key, limit.Max, limit.Window, permits);
        if (!taken.IsAllowed) return Denied(http, taken, limit.Window);
        IResult result;
        try
        {
            result = await action();
        }
        catch
        {
            await lease.ReleaseAllAsync();
            throw;
        }
        if (!keepOnFailure && !IsSuccess(result)) await lease.ReleaseAllAsync();
        return result;
    }

    // Several limits on one request (B1: user + IP); the lease gives every permit back when the caller says so.
    public static async Task<IResult?> TakeAsync(RateLimitPermits lease, HttpContext http, BuddyRateLimit limit, int permits = 1)
    {
        var taken = await lease.TakeAsync(limit.Key, limit.Max, limit.Window, permits);
        return taken.IsAllowed ? null : Denied(http, taken, limit.Window);
    }

    public static bool IsSuccess(IResult result)
        => result is not IStatusCodeHttpResult { StatusCode: { } code } || code is >= 200 and < 300;

    public static IResult Error(string type, object? data = null)
        => ErrorResponse(BuddyErrorTypes.StatusOf(type), BuddyErrorTypes.MessageOf(type), type, data);

    public static IResult Error(int status, string type, object? data = null)
        => ErrorResponse(status, BuddyErrorTypes.MessageOf(type), type, data);

    public static IResult TripNotFound() => ErrorResponse(404, "Trip not found", BuddyErrorTypes.TripNotFound);

    public static IResult Invalid(FluentValidation.Results.ValidationResult validation)
        => BadRequestResponse(validation.Errors.FirstOrDefault()?.ErrorMessage ?? BuddyErrorTypes.MessageOf(BuddyErrorTypes.BadRequest));

    public static int ClampLimit(int? limit, int defaultValue, int max)
        => limit is null ? defaultValue : Math.Clamp(limit.Value, 1, max);

    // A present but unreadable cursor is a 400; null or empty = first page.
    public static bool TryKeyset(string? cursor, out (DateTime At, Guid Id)? key)
    {
        key = null;
        if (string.IsNullOrEmpty(cursor)) return true;
        if (!KeysetCursor.TryDecode(cursor, out var at, out var id)) return false;
        key = (at, id);
        return true;
    }

    public static bool TryNameCursor(string? cursor, out (string LowerName, Guid Id)? key)
    {
        key = null;
        if (string.IsNullOrEmpty(cursor)) return true;
        if (!BuddyCursors.TryDecodeName(cursor, out var name, out var id)) return false;
        key = (name, id);
        return true;
    }

    public static bool TryExpenseCursor(string? cursor, out (int Day, DateTime CreatedAt, Guid Id)? key)
    {
        key = null;
        if (string.IsNullOrEmpty(cursor)) return true;
        if (!BuddyCursors.TryDecodeExpense(cursor, out var day, out var at, out var id)) return false;
        key = (day, at, id);
        return true;
    }

    public static IResult BadCursor() => BadRequestResponse("Invalid cursor");

    private static IResult Denied(HttpContext http, RateLimitResult limit, TimeSpan window)
    {
        http.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling((limit.RetryAfter ?? window).TotalSeconds)).ToString();
        return ErrorResponse(429, BuddyErrorTypes.MessageOf(BuddyErrorTypes.TooManyRequests), BuddyErrorTypes.TooManyRequests);
    }
}
