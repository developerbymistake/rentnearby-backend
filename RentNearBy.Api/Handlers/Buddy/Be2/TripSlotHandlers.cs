using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Buddy.Be2;

// GET /trips/slots: advisory preflight of the trip limit; create and accept are the real gate.
public static class TripSlotHandlers
{
    public static async Task<IResult> Get(ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] ApplicationDbContext db, [FromServices] ISeekerLimits seekerLimits, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.TripView(me)) is { } denied) return denied;
        var limit = (await seekerLimits.GetAsync(me, ct)).ActiveTrips;
        return OkResponse(await TripSlots.ReadAsync(db, me, limit, ct));
    }
}
