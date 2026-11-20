using System.Security.Claims;
using FluentValidation;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Buddy.Be1;

// B1 to B11 (contract 2.1). Thin: validate, rate limit, call the service, map the answer.
public static class BuddyHandlers
{
    public const string BoxReceived = "received";
    public const string BoxSent = "sent";

    public static async Task<IResult> Find(
        FindBuddyRequest request, IValidator<FindBuddyRequest> validator, ClaimsPrincipal principal,
        BuddyFindService finder, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return BuddyHttp.Invalid(validation);

        var lease = new RateLimitPermits(limiter);
        if (await BuddyHttp.TakeAsync(lease, http, BuddyRateLimitKeys.Find(me)) is { } denied) return denied;
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (await BuddyHttp.TakeAsync(lease, http, BuddyRateLimitKeys.FindIp(ip)) is { } ipDenied)
        {
            await lease.ReleaseAllAsync();
            return ipDenied;
        }
        var found = await finder.FindAsync(me, request.Query, ct);
        return found.Found ? OkResponse(found) : OkResponse(new NotFoundBuddyResponse { Found = false });
    }

    public static async Task<IResult> SendRequest(
        SendBuddyRequest request, IValidator<SendBuddyRequest> validator, ClaimsPrincipal principal,
        BuddyRequestService requests, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return BuddyHttp.Invalid(validation);
        if (request.ToUserId == me) return BuddyHttp.Error(BuddyErrorTypes.SelfRequest);

        var lease = new RateLimitPermits(limiter);
        if (await BuddyHttp.TakeAsync(lease, http, BuddyRateLimitKeys.Request(me)) is { } denied) return denied;
        BuddyResult<SendOutcome> result;
        try
        {
            result = await requests.SendAsync(me, request.ToUserId, request.Source ?? BuddyRequestSources.Search, request.ClientRequestId, ct);
        }
        catch
        {
            await lease.ReleaseAllAsync();
            throw;
        }
        if (!result.Ok || result.Value!.Kind != SendKind.Created) await lease.ReleaseAllAsync();
        if (!result.Ok) return BuddyHttp.Error(result.ErrorType!, result.ErrorData);
        var outcome = result.Value!;
        return outcome.Kind switch
        {
            SendKind.Created => CreatedResponse(outcome.Request!, $"/api/v1/buddies/requests/{outcome.Request!.RequestId}"),
            SendKind.Existing => OkResponse(outcome.Request!),
            _ => OkResponse(outcome.Accepted!),
        };
    }

    public static async Task<IResult> GetRequests(
        ClaimsPrincipal principal, BuddyListService lists, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? box = null, string? cursor = null, int? limit = null)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (box is not (BoxReceived or BoxSent)) return BadRequestResponse("Box must be received or sent");
        if (!BuddyHttp.TryKeyset(cursor, out var key)) return BuddyHttp.BadCursor();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.BuddyRead(me)) is { } denied) return denied;
        var take = BuddyHttp.ClampLimit(limit, BuddyLimits.RequestsPageMax, BuddyLimits.RequestsPageMax);
        return box == BoxReceived
            ? OkResponse(await lists.ReceivedAsync(me, key, take, ct))
            : OkResponse(await lists.SentAsync(me, key, take, ct));
    }

    public static Task<IResult> Accept(
        Guid id, ClaimsPrincipal principal, BuddyRequestService requests, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => WriteAsync(principal, limiter, http, async me =>
        {
            var result = await requests.AcceptAsync(me, id, ct);
            return result.Ok ? OkResponse(result.Value!) : BuddyHttp.Error(result.ErrorType!, result.ErrorData);
        });

    public static Task<IResult> Decline(
        Guid id, ClaimsPrincipal principal, BuddyRequestService requests, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => WriteAsync(principal, limiter, http, async me =>
            await requests.DeclineAsync(me, id, ct) ? NoContentResponse() : BuddyHttp.Error(BuddyErrorTypes.RequestEnded));

    public static Task<IResult> Cancel(
        Guid id, ClaimsPrincipal principal, BuddyRequestService requests, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => WriteAsync(principal, limiter, http, async me =>
        {
            var result = await requests.CancelAsync(me, id, ct);
            return result.Ok ? NoContentResponse() : BuddyHttp.Error(result.ErrorType!, result.ErrorData);
        });

    public static async Task<IResult> GetBuddies(
        ClaimsPrincipal principal, BuddyListService lists, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? cursor = null, int? limit = null, Guid? forTripId = null)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (!BuddyHttp.TryNameCursor(cursor, out var key)) return BuddyHttp.BadCursor();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.BuddyRead(me)) is { } denied) return denied;
        var take = BuddyHttp.ClampLimit(limit, BuddyLimits.BuddiesPageDefault, BuddyLimits.BuddiesPageMax);
        var page = await lists.BuddiesAsync(me, key, take, forTripId, ct);
        return page is null ? BuddyHttp.TripNotFound() : OkResponse(page);
    }

    public static async Task<IResult> Seen(
        BuddySeenRequest request, IValidator<BuddySeenRequest> validator, ClaimsPrincipal principal,
        BuddyListService lists, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out _)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return BuddyHttp.Invalid(validation);
        return await WriteAsync(principal, limiter, http, async me =>
        {
            await lists.SeenAsync(me, request.UserIds, ct);
            return NoContentResponse();
        });
    }

    public static Task<IResult> Remove(
        Guid userId, ClaimsPrincipal principal, BuddyListService lists, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => WriteAsync(principal, limiter, http, async me =>
        {
            await lists.RemoveAsync(me, userId, ct);
            return NoContentResponse();
        });

    public static async Task<IResult> Summary(
        ClaimsPrincipal principal, BuddyListService lists, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.BuddyRead(me)) is { } denied) return denied;
        return OkResponse(await lists.SummaryAsync(me, ct));
    }

    private static async Task<IResult> WriteAsync(ClaimsPrincipal principal, IRateLimitService limiter, HttpContext http, Func<Guid, Task<IResult>> action)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.BuddyWrite(me), () => action(me));
    }
}
