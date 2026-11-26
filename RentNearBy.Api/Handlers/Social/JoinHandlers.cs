using System.Security.Claims;
using FluentValidation;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Social;

// Contract 2.5: join requests.
public static class JoinHandlers
{
    // The JoinSend permit is given back on a refusal (ActionLimitAsync default).
    public static async Task<IResult> Send(
        Guid tripId, SendJoinRequest request, IValidator<SendJoinRequest> validator, ClaimsPrincipal principal, JoinRequestService joins,
        ApplicationDbContext db, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        return await SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.JoinSend(ProfileHandlers.BuddyCaller(principal)), true,
            async me => SocialHttp.From(await joins.SendAsync(me, tripId, request, ct)), ct);
    }

    public static Task<IResult> CancelMine(
        Guid tripId, ClaimsPrincipal principal, JoinRequestService joins, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me =>
            {
                await joins.CancelMineAsync(me, tripId, ct);
                return SocialHttp.Empty();
            }, ct);

    public static Task<IResult> List(
        Guid tripId, ClaimsPrincipal principal, JoinRequestService joins, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.ReadAsync(principal, limiter, http, async me =>
            await joins.ListAsync(me, tripId, ct) is { } page ? OkResponse(page) : SocialHttp.Error(SocialErrorTypes.TripNotFound));

    public static Task<IResult> Accept(
        Guid id, AcceptJoinRequest request, ClaimsPrincipal principal, JoinRequestService joins, ApplicationDbContext db,
        IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me => SocialHttp.From(await joins.AcceptAsync(me, id, request.AddToBuddies, ct)), ct);

    public static Task<IResult> Decline(
        Guid id, ClaimsPrincipal principal, JoinRequestService joins, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me =>
            {
                var result = await joins.DeclineAsync(me, id, ct);
                return result.Ok ? SocialHttp.Empty() : SocialHttp.Error(result.ErrorType!, result.ErrorData);
            }, ct);
}
