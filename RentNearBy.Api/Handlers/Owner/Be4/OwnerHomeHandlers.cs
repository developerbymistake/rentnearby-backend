using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Extensions;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be4;

// H1 GET /api/v1/owner/home: the owner Home of the caller's role (Stay Owner or Taxi Driver). Seeker = 403 RoleMismatch;
// no listing = 404 ListingNotFound (the app shows the setup entry).
public static class OwnerHomeHandlers
{
    public static async Task<IResult> Get(
        ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter, IUnitOfWork unitOfWork, IMemoryCache cache,
        IOptions<TripPlannerOptions> planner, IServiceProvider sp, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var userId)) return UnauthorizedResponse();
        var kind = KindOf(principal.FindFirst(ClaimTypes.Role)?.Value);
        if (kind is null) return OwnerHttp.Refuse(OwnerErrorTypes.RoleMismatch);
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.HomeRead(userId)) is { } denied) return denied;

        var tabs = await AppTabCache.GetAsync(unitOfWork, cache);
        var env = new OwnerHomeEnv(TabState.IsKindActive(tabs, kind), planner.Value.Enabled);
        var home = kind == ListingKinds.Stay
            ? await sp.GetRequiredService<StayOwnerHomeReader>().ReadAsync(userId, env, ct)
            : await sp.GetRequiredService<TaxiDriverHomeReader>().ReadAsync(userId, env, ct);
        return home is null ? OwnerHttp.ListingNotFound() : OkResponse(home);
    }

    public static string? KindOf(string? roleKey) => roleKey switch
    {
        UserRoles.StayOwner => ListingKinds.Stay,
        UserRoles.TaxiDriver => ListingKinds.Taxi,
        _ => null,
    };
}
