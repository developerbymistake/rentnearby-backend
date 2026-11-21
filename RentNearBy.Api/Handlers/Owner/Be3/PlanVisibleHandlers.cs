using System.Security.Claims;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be3;

// V1 / V2 (contract 2.4, AU-9): choose what stays visible. The answer carries the visible and hidden arrays, the meters and the
// changes left today, so the screen needs no re-read. Cache generations move after commit when something changed.
public static class PlanVisibleHandlers
{
    public static async Task<IResult> SaveStay(
        StayPlanVisibleRequest request, ClaimsPrincipal principal, PlanVisibleService service,
        IPlanEntitlementService entitlements, IRateLimitService limiter, HttpContext http, IServiceProvider sp)
    {
        if (!OwnerHttp.TryOwner(principal, out var userId)) return UnauthorizedResponse();
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(userId), async () =>
        {
            var outcome = await service.SaveStayAsync(userId, request.PhotoIds ?? [], request.RoomIds ?? [], http.RequestAborted);
            if (outcome.Error != null) return OwnerHttp.Refuse(outcome.Error, outcome.ErrorData);
            if (outcome.Changed) await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), outcome.DistrictId);
            var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Stay);
            return OkResponse(await service.StayAnswerAsync(outcome.ListingId, limits, outcome.SavesUsedToday, http.RequestAborted));
        });
    }

    public static async Task<IResult> SaveTaxi(
        TaxiPlanVisibleRequest request, ClaimsPrincipal principal, PlanVisibleService service,
        IPlanEntitlementService entitlements, ICityResolver resolver, IRateLimitService limiter, HttpContext http, IServiceProvider sp)
    {
        if (!OwnerHttp.TryOwner(principal, out var userId)) return UnauthorizedResponse();
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(userId), async () =>
        {
            var outcome = await service.SaveTaxiAsync(userId, request.RouteIds ?? [], http.RequestAborted);
            if (outcome.Error != null)
            {
                await service.AfterTaxiCommitAsync(outcome.ListingId, changed: false);
                return OwnerHttp.Refuse(outcome.Error, outcome.ErrorData);
            }
            await service.AfterTaxiCommitAsync(outcome.ListingId, outcome.Changed);
            if (outcome.Changed) await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), outcome.DistrictId);
            var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Taxi);
            return OkResponse(await service.TaxiAnswerAsync(resolver, outcome.ListingId, limits, outcome.SavesUsedToday, http.RequestAborted));
        });
    }
}
