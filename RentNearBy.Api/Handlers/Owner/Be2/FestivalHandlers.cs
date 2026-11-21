using System.Security.Claims;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be2;

// F1 GET /api/v1/festivals?stateName&from&to (any signed-in user): owner chips, names and reminders only.
public static class FestivalHandlers
{
    public static async Task<IResult> List(
        ClaimsPrincipal principal, FestivalService service, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? stateName = null, DateOnly? from = null, DateOnly? to = null)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.FestivalRead(me)) is { } denied) return denied;
        if (stateName is { Length: > OwnerLimits.StateNameMax }) return BadRequestResponse("State name is too long.");
        return Be2Answers.Of(await service.ListAsync(stateName, from, to, ct));
    }
}
