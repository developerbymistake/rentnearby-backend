using System.Globalization;
using System.Security.Claims;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Contract 1.5 P1: public, inside the tab-gated /api/v1/taxis group, the feed rate limit (120 a minute per caller).
public static class PopularRouteHandlers
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int PerMinute = 120;

    public static async Task<IResult> Get(
        PopularRouteReader reader, IRateLimitService limiter, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var caller = UsersHandlers.TryGetUserId(principal, out var id) ? id.ToString() : http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var rl = await limiter.CheckAsync($"taxi:feed:{caller}", PerMinute, Window);
        if (!rl.IsAllowed)
        {
            http.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling((rl.RetryAfter ?? Window).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return TooManyRequestsResponse();
        }
        return OkResponse(await reader.ReadAsync(ct));
    }
}
