using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be1;

// Contract 1.3 A1 / A2 under /api/v1/taxis/me (policy CanListTaxis, not tab-gated). The taxi comes ONLY from the JWT user id.
public static class TaxiDayOffHandlers
{
    private const string Kind = ListingKinds.Taxi;

    public static async Task<IResult> Read(
        ClaimsPrincipal principal, OwnerCalendarReader reader, TaxiDayOffService days, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
        if (ctx == null) return fail!;
        return OkResponse(await days.ReadAsync(ctx, ct));
    }

    public static async Task<IResult> Save(
        TaxiDaysOffSaveRequest? request, ClaimsPrincipal principal, OwnerCalendarReader reader, TaxiDayOffService days,
        IRateLimitService limiter, HttpContext http, [FromServices] IConnectionMultiplexer? redis, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var parsed = TaxiDayOffRules.Parse(request);
        if (!parsed.Ok) return OwnerHttp.Refuse(parsed.ErrorType!, parsed.Message!);

        var saved = false;
        var result = await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
            if (ctx == null) return fail!;
            var outcome = await days.SaveAsync(ctx, parsed.Changes!, ct);
            if (!outcome.Ok) return OwnerHttp.Refuse(outcome.Error!.Type, outcome.Error.Message, outcome.Error.Data);
            saved = true;
            return OkResponse(outcome.Value!);
        });
        if (saved) await FeedCache.BumpAsync(redis, [ListingCacheKeys.TaxiAvailGen]);
        return result;
    }
}
