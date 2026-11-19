using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.TaxiRequests;

// O16: light per-user limits on the taxi request routes, one key for reads (status poll, lists) and one for actions, the
// same keys for the traveller and the driver side. 429 + Retry-After. An action that did not go through (any refusal or
// an exception) gives its permit back.
public static class TaxiRateLimits
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static string ReadKey(Guid userId) => $"taxi:read:{userId}";

    public static string ActionKey(Guid userId) => $"taxi:action:{userId}";

    public static async Task<IResult?> ReadAsync(IRateLimitService limiter, HttpContext http, Guid userId)
    {
        var limit = await limiter.CheckAsync(ReadKey(userId), TaxiDispatchRules.ReadsPerMinute, Window);
        return limit.IsAllowed ? null : Denied(http, limit);
    }

    public static async Task<IResult> ActionAsync(IRateLimitService limiter, HttpContext http, Guid userId, Func<Task<IResult>> action)
    {
        var permits = new RateLimitPermits(limiter);
        var limit = await permits.TakeAsync(ActionKey(userId), TaxiDispatchRules.ActionsPerMinute, Window);
        if (!limit.IsAllowed) return Denied(http, limit);
        IResult result;
        try
        {
            result = await action();
        }
        catch
        {
            await permits.ReleaseAllAsync();
            throw;
        }
        if (!IsSuccess(result)) await permits.ReleaseAllAsync();
        return result;
    }

    public static bool IsSuccess(IResult result)
        => result is not IStatusCodeHttpResult { StatusCode: { } code } || code is >= 200 and < 300;

    private static IResult Denied(HttpContext http, RateLimitResult limit)
    {
        http.Response.Headers["Retry-After"] = ((int)Math.Ceiling((limit.RetryAfter ?? Window).TotalSeconds)).ToString();
        return TooManyRequestsResponse();
    }
}
