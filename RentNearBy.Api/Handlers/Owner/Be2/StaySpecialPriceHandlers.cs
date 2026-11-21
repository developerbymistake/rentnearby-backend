using System.Security.Claims;
using FluentValidation;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be2;

// P1..P6 under /api/v1/stays/me/special-prices (CanListStays, no tab gate). Thin: caller, rate limit, validate, service, answer.
// A state-changing write bumps the stay cache generation after commit (the traveller lists carry the rules).
public static class StaySpecialPriceHandlers
{
    public static async Task<IResult> List(
        ClaimsPrincipal principal, StaySpecialPriceService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        return Be2Answers.Of(await service.ListAsync(me, ct));
    }

    public static async Task<IResult> Create(
        CreateSpecialPriceRequest request, IValidator<CreateSpecialPriceRequest> validator, ClaimsPrincipal principal,
        StaySpecialPriceService service, IRateLimitService limiter, IServiceProvider sp, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
            await Be2Answers.WriteAsync(await service.CreateAsync(me, request, ct), sp));
    }

    public static async Task<IResult> Update(
        Guid id, UpdateSpecialPriceRequest request, IValidator<UpdateSpecialPriceRequest> validator, ClaimsPrincipal principal,
        StaySpecialPriceService service, IRateLimitService limiter, IServiceProvider sp, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
            await Be2Answers.WriteAsync(await service.UpdateAsync(me, id, request, ct), sp));
    }

    public static async Task<IResult> Delete(
        Guid id, ClaimsPrincipal principal, StaySpecialPriceService service, IRateLimitService limiter, IServiceProvider sp,
        HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var result = await service.DeleteAsync(me, id, ct);
            if (!result.Ok) return Be2Answers.Error(result);
            await Be2Answers.BustAsync(result.Changed, result.DistrictId, sp);
            return NoContentResponse();
        });
    }

    public static async Task<IResult> Pause(
        Guid id, PauseSpecialPriceRequest request, IValidator<PauseSpecialPriceRequest> validator, ClaimsPrincipal principal,
        StaySpecialPriceService service, IRateLimitService limiter, IServiceProvider sp, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
            await Be2Answers.WriteAsync(await service.PauseAsync(me, id, request, ct), sp));
    }

    public static async Task<IResult> MakeLive(
        Guid id, LiveSpecialPriceRequest request, IValidator<LiveSpecialPriceRequest> validator, ClaimsPrincipal principal,
        StaySpecialPriceService service, IRateLimitService limiter, IServiceProvider sp, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
            await Be2Answers.WriteAsync(await service.MakeLiveAsync(me, id, request, ct), sp));
    }
}

// Maps a lane BE2 service answer to the envelope.
internal static class Be2Answers
{
    public static IResult Of<T>(Be2Result<T> result) => result.Ok ? OkResponse(result.Value) : Error(result);

    public static IResult Error<T>(Be2Result<T> result) => result.ErrorType switch
    {
        Be2Result<T>.NotFound => NotFoundResponse(result.Message ?? "Not found"),
        OwnerErrorTypes.BadRequest => BadRequestResponse(result.Message ?? OwnerErrorTypes.MessageOf(OwnerErrorTypes.BadRequest)),
        { } type => OwnerHttp.Refuse(type, result.Message ?? OwnerErrorTypes.MessageOf(type), result.Data),
        _ => OkResponse(result.Value),
    };

    public static async Task<IResult> WriteAsync<T>(Be2Result<T> result, IServiceProvider sp)
    {
        if (!result.Ok) return Error(result);
        await BustAsync(result.Changed, result.DistrictId, sp);
        return OkResponse(result.Value);
    }

    // The traveller lists carry the rules, so a changed rule bumps the stay generation (and the trip city list) after commit.
    public static Task BustAsync(bool changed, Guid? districtId, IServiceProvider sp)
        => changed ? StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), districtId) : Task.CompletedTask;
}
