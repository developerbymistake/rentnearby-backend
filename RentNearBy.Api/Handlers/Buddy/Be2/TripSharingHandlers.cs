using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Buddy.Be2;

// Trip sharing S1 to S17 (contract 2.2). Thin: caller, limit, validate, call the service, map the result.
public static class TripSharingHandlers
{
    // S1
    public static async Task<IResult> Members(Guid id, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripMemberService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.TripView(me)) is { } denied) return denied;
        return From(await service.GetMembersAsync(id, me, ct));
    }

    // S2: permits = number of ids; the permits of people not invited are given back.
    public static async Task<IResult> Invite(Guid id, TripInviteRequest request, ClaimsPrincipal principal, HttpContext http,
        IRateLimitService limiter, IValidator<TripInviteRequest> validator, [FromServices] TripInviteService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return BuddyHttp.Invalid(validation);

        var limit = BuddyRateLimitKeys.TripInvite(me);
        var lease = new RateLimitPermits(limiter);
        if (await BuddyHttp.TakeAsync(lease, http, limit, request.UserIds.Count) is { } denied) return denied;
        SharingResult<TripInviteResultDto> result;
        try
        {
            result = await service.InviteAsync(id, me, request.UserIds, ct);
        }
        catch
        {
            await lease.ReleaseAllAsync();
            throw;
        }
        var invited = result.Ok ? result.Value!.Results.Count(r => r.Outcome == InviteOutcomes.Invited)
            : BuddyHttp.GivesPermitBack(result.ErrorType!) ? 0 : request.UserIds.Count;
        if (invited < request.UserIds.Count)
        {
            await lease.ReleaseAllAsync();
            if (invited > 0) await limiter.CheckAsync(limit.Key, limit.Max, limit.Window, invited);
        }
        return From(result);
    }

    // S3
    public static Task<IResult> CancelInvite(Guid id, Guid userId, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripInviteService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return Task.FromResult(UnauthorizedResponse());
        return BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.TripMembers(me),
            async () => From(await service.CancelAsync(id, me, userId, ct)));
    }

    // S7
    public static async Task<IResult> Preview(Guid memberId, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripInviteService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.TripView(me)) is { } denied) return denied;
        return From(await service.PreviewAsync(memberId, me, ct));
    }

    // S8
    public static Task<IResult> Accept(Guid memberId, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripInviteService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return Task.FromResult(UnauthorizedResponse());
        return BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.TripJoin(me),
            async () => From(await service.AcceptAsync(memberId, me, ct)));
    }

    // S9
    public static Task<IResult> Decline(Guid memberId, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripInviteService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return Task.FromResult(UnauthorizedResponse());
        return BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.TripJoin(me),
            async () => From(await service.DeclineAsync(memberId, me, ct)));
    }

    // S12
    public static Task<IResult> Remove(Guid id, Guid userId, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripMemberService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return Task.FromResult(UnauthorizedResponse());
        return BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.TripMembers(me),
            async () => From(await service.RemoveAsync(id, me, userId, ct)));
    }

    // S13
    public static Task<IResult> Leave(Guid id, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripMemberService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return Task.FromResult(UnauthorizedResponse());
        return BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.TripJoin(me),
            async () => From(await service.LeaveAsync(id, me, ct)));
    }

    // S14: ETag + If-None-Match (304).
    public static async Task<IResult> View(Guid id, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripViewerReader reader, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.TripView(me)) is { } denied) return denied;
        var result = await reader.GetAsync(id, me, http.Request.Headers.IfNoneMatch.ToString(), ct);
        if (!result.Ok) return Fail(result);
        http.Response.Headers.ETag = result.Value!.ETag;
        http.Response.Headers.CacheControl = "private, no-cache";
        return result.Value.NotModified ? Results.StatusCode(StatusCodes.Status304NotModified) : OkResponse(result.Value.View);
    }

    // S15
    public static async Task<IResult> Shared(string? state, string? cursor, int? limit, ClaimsPrincipal principal, HttpContext http,
        IRateLimitService limiter, [FromServices] TripSharedListReader reader, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.TripView(me)) is { } denied) return denied;
        var listState = string.IsNullOrEmpty(state) ? SharedListStates.Upcoming : state;
        if (!SharedListStates.IsValid(listState)) return BadRequestResponse("state must be upcoming or past");
        if (!BuddyHttp.TryKeyset(cursor, out var key)) return BuddyHttp.BadCursor();
        var take = BuddyHttp.ClampLimit(limit, BuddyLimits.SharedTripsPageMax, BuddyLimits.SharedTripsPageMax);
        return OkResponse(await reader.GetAsync(me, listState, key, take, ct));
    }

    // S16
    public static async Task<IResult> ExpenseCount(Guid id, Guid userId, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripMemberService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.TripView(me)) is { } denied) return denied;
        return From(await service.ExpenseCountAsync(id, me, userId, ct));
    }

    // S17
    public static async Task<IResult> InviteOptions(Guid? userId, ClaimsPrincipal principal, HttpContext http, IRateLimitService limiter,
        [FromServices] TripMemberService service, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.TripView(me)) is { } denied) return denied;
        if (userId is not { } other || other == Guid.Empty) return BadRequestResponse("userId is required");
        return From(await service.InviteOptionsAsync(me, other, ct));
    }

    public static IResult From<T>(SharingResult<T> result) => result.Ok ? OkResponse(result.Value) : Fail(result);

    public static IResult Fail<T>(SharingResult<T> result)
    {
        if (result.ErrorType == BuddyErrorTypes.TripNotFound) return BuddyHttp.TripNotFound();
        return result.Status is { } status
            ? BuddyHttp.Error(status, result.ErrorType!, result.ErrorData)
            : BuddyHttp.Error(result.ErrorType!, result.ErrorData);
    }
}
