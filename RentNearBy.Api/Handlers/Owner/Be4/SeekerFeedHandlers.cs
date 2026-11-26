using System.Security.Claims;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be4;

// H2 GET /api/v1/home/seeker-feed (SeekerOnly): trip card, status line and plan slots in one call.
public static class SeekerFeedHandlers
{
    public static async Task<IResult> Get(
        ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter, SeekerFeedReader reader, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var userId)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.SeekerFeedRead(userId)) is { } denied) return denied;
        return OkResponse(await reader.ReadAsync(userId, ct));
    }
}
