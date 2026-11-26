using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be1;

// Stay blocked ranges K1 to K4 under /api/v1/stays/me/blocked-ranges (contract 2.2). A change bumps the stay cache generation
// after commit (travellers see "Full on your dates" from the next read).
public static class StayBlockedRangeHandlers
{
    private const string Kind = ListingKinds.Stay;

    public static StayBlockedRangeDto ToDto(BlockedRangeRow b)
        => new(b.Id, b.DateFrom, b.DateTo, BlockedRangeRules.Nights(b.DateFrom, b.DateTo), b.Reason, b.Version);

    public static async Task<IResult> List(
        ClaimsPrincipal principal, OwnerCalendarReader reader, BlockedRangeService blocks, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
        if (ctx == null) return fail!;
        var rows = await blocks.ListAsync(ctx, ct);
        return OkResponse(new StayBlockedRangeListDto(rows.Select(ToDto).ToList(), null, BlockedRangeService.Meters(ctx, rows.Count), ctx.Limits.PaymentEnabled));
    }

    public static async Task<IResult> Create(
        StayBlockedRangeRequest request, bool? confirm, IValidator<StayBlockedRangeRequest> validator, ClaimsPrincipal principal,
        OwnerCalendarReader reader, BlockedRangeService blocks, IRateLimitService limiter, HttpContext http, [FromServices] IConnectionMultiplexer? redis, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
            if (ctx == null) return fail!;
            var result = await blocks.CreateAsync(ctx, request.Id, request.DateFrom!.Value, request.DateTo!.Value, OwnerCalendarRules.Clean(request.Reason), confirm == true, ct);
            return await AnswerAsync(result, ctx.DistrictId, redis);
        });
    }

    public static async Task<IResult> Update(
        Guid id, StayBlockedRangeRequest request, bool? confirm, IValidator<StayBlockedRangeRequest> validator, ClaimsPrincipal principal,
        OwnerCalendarReader reader, BlockedRangeService blocks, IRateLimitService limiter, HttpContext http, [FromServices] IConnectionMultiplexer? redis, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
            if (ctx == null) return fail!;
            var result = await blocks.UpdateAsync(ctx, id, request.Version, request.DateFrom!.Value, request.DateTo!.Value, OwnerCalendarRules.Clean(request.Reason), confirm == true, ct);
            return await AnswerAsync(result, ctx.DistrictId, redis);
        });
    }

    public static async Task<IResult> Delete(
        Guid id, ClaimsPrincipal principal, OwnerCalendarReader reader, BlockedRangeService blocks, IRateLimitService limiter, HttpContext http,
        [FromServices] IConnectionMultiplexer? redis, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
            if (ctx == null) return fail!;
            var outcome = await blocks.DeleteAsync(ctx, id, ct);
            if (outcome == BlockedRangeService.DeleteOutcome.NotHis) return NotFoundResponse("Block not found");
            if (outcome == BlockedRangeService.DeleteOutcome.Deleted) await StayCache.InvalidateAsync(redis, ctx.DistrictId);
            return NoContentResponse();
        });
    }

    private static async Task<IResult> AnswerAsync(Be1Result<BlockedRangeWrite> result, Guid? districtId, IConnectionMultiplexer? redis)
    {
        if (!result.Ok) return OwnerCalendarHttp.Error(result.Error!, StayCalendarEntryDto.From, ToDto);
        var w = result.Value!;
        if (w.Changed) await StayCache.InvalidateAsync(redis, districtId);
        return OkResponse(new StayBlockedRangeWriteDto(ToDto(w.Block), w.ConfirmedInside, w.OpenEnquiriesInside, w.Meters));
    }
}
