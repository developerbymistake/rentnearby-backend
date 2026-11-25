using System.Security.Claims;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner;

// Shared HTTP helpers of the owner roles (pattern BuddyHttp): caller id, rate limits, typed refusals, cursors.
public static class OwnerHttp
{
    // The caller id comes from the JWT only (contract 0.3.4).
    public static bool TryOwner(ClaimsPrincipal principal, out Guid userId) => UsersHandlers.TryGetUserId(principal, out userId);

    public static async Task<IResult?> ReadLimitAsync(IRateLimitService limiter, HttpContext http, OwnerRateLimit limit)
    {
        var result = await limiter.CheckAsync(limit.Key, limit.Max, limit.Window);
        return result.IsAllowed ? null : Denied(http, result, limit.Window);
    }

    // Writes: permits are given back for a business refusal (409/422/400) or an exception; 401/403/404 keep the permit.
    // The attempt bucket (FB-52) is taken first and never given back, so a loop of refused writes still hits 429.
    public static async Task<IResult> WriteLimitAsync(IRateLimitService limiter, HttpContext http, OwnerRateLimit limit, Func<Task<IResult>> action)
    {
        if (limit.Attempt is { } attempt)
        {
            var tried = await limiter.CheckAsync(attempt.Key, attempt.Max, attempt.Window);
            if (!tried.IsAllowed) return Denied(http, tried, attempt.Window);
        }
        var lease = new RateLimitPermits(limiter);
        var taken = await lease.TakeAsync(limit.Key, limit.Max, limit.Window);
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
        if (GivesPermitBack(result)) await lease.ReleaseAllAsync();
        return result;
    }

    public static bool IsSuccess(IResult result)
        => result is not IStatusCodeHttpResult { StatusCode: { } code } || code is >= 200 and < 300;

    public static bool GivesPermitBack(IResult result)
        => !IsSuccess(result) && result is IStatusCodeHttpResult { StatusCode: not (401 or 403 or 404) };

    public static IResult Refuse(string type, object? data = null)
        => ErrorResponse(OwnerErrorTypes.StatusOf(type), OwnerErrorTypes.MessageOf(type), type, data);

    public static IResult Refuse(string type, string message, object? data = null)
        => ErrorResponse(OwnerErrorTypes.StatusOf(type), message, type, data);

    // Every limit refusal: the per-key type, the two-line message rule and data { key, limit, used, canUpgrade }.
    public static IResult LimitReached(string type, string key, int limit, int used, bool canUpgrade)
        => ErrorResponse(OwnerErrorTypes.StatusOf(type), OwnerErrorTypes.LimitMessage(type, limit, canUpgrade), type,
            new PlanLimitRefusalDto(key, limit, used, canUpgrade));

    public static IResult LimitReached(string type, string key, int used, PlanLimits limits)
        => LimitReached(type, key, limits.Get(key), used, limits.CanUpgrade);

    public static IResult FeatureLocked(string key) => Refuse(OwnerErrorTypes.PlanFeatureLocked, new { key });

    public static IResult ListingNotFound() => Refuse(OwnerErrorTypes.ListingNotFound);

    // 409 PlanRowInactive: a write to a row the plan made inactive; Delete stays open.
    public static IResult RowInactive() => Refuse(OwnerErrorTypes.PlanRowInactive);

    public static IResult StaleEdit<T>(T current) => Refuse(OwnerErrorTypes.StaleEdit, new StaleEditDto<T>(current));

    public static IResult Invalid(FluentValidation.Results.ValidationResult validation)
        => BadRequestResponse(validation.Errors.FirstOrDefault()?.ErrorMessage ?? OwnerErrorTypes.MessageOf(OwnerErrorTypes.BadRequest));

    public static IResult BadCursor() => BadRequestResponse("Invalid cursor");

    // A present but unreadable cursor is a 400; null or empty = first page.
    public static bool TryDateCursor(string? cursor, out (DateOnly Date, Guid Id)? key)
    {
        key = null;
        if (string.IsNullOrEmpty(cursor)) return true;
        if (!OwnerCursors.TryDecodeDate(cursor, out var date, out var id)) return false;
        key = (date, id);
        return true;
    }

    public static bool TryNumberCursor(string? cursor, out (long Value, Guid Id)? key)
    {
        key = null;
        if (string.IsNullOrEmpty(cursor)) return true;
        if (!OwnerCursors.TryDecodeNumber(cursor, out var value, out var id)) return false;
        key = (value, id);
        return true;
    }

    public static bool TryTimeCursor(string? cursor, out (DateTime At, Guid Id)? key)
    {
        key = null;
        if (string.IsNullOrEmpty(cursor)) return true;
        if (!KeysetCursor.TryDecode(cursor, out var at, out var id)) return false;
        key = (at, id);
        return true;
    }

    public static IResult Denied(HttpContext http, RateLimitResult limit, TimeSpan window)
    {
        http.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling((limit.RetryAfter ?? window).TotalSeconds)).ToString();
        return ErrorResponse(429, OwnerErrorTypes.MessageOf(OwnerErrorTypes.TooManyRequests), OwnerErrorTypes.TooManyRequests);
    }
}
