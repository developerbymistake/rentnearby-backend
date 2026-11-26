using System.Security.Claims;
using RentNearBy.Api.Handlers.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Social;

// Shared HTTP helpers of the social routes: typed errors (SocialErrorTypes), read and write wrappers with the rate limits and the
// ProfileRequired / SocialOnHold guard.
public static class SocialHttp
{
    public static IResult Error(string type, object? data = null)
        => ErrorResponse(SocialErrorTypes.StatusOf(type), SocialErrorTypes.MessageOf(type), type, data);

    public static IResult From<T>(BuddyResult<T> result) => result.Ok ? OkResponse(result.Value!) : Error(result.ErrorType!, result.ErrorData);

    public static IResult Empty() => OkResponse(new { });

    public static async Task<IResult> ReadAsync(
        ClaimsPrincipal principal, IRateLimitService limiter, HttpContext http, Func<Guid, Task<IResult>> action)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, SocialRateLimits.Read(me)) is { } denied) return denied;
        return await action(me);
    }

    // touchesOther = follow, join send, room create, publish: the social hold applies (403 SocialOnHold {until}).
    public static async Task<IResult> WriteAsync(
        ClaimsPrincipal principal, ApplicationDbContext db, IRateLimitService limiter, HttpContext http, BuddyRateLimit limit,
        bool touchesOther, Func<Guid, Task<IResult>> action, CancellationToken ct, bool keepOnFailure = false)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await SocialSql.GuardAsync(db, me, touchesOther, ct) is { } fail) return Error(fail.Type, fail.Data);
        return await BuddyHttp.ActionLimitAsync(limiter, http, limit, () => action(me), keepOnFailure: keepOnFailure);
    }

    public static IResult Invalid(FluentValidation.Results.ValidationResult validation)
        => BadRequestResponse(validation.Errors.FirstOrDefault()?.ErrorMessage ?? "Invalid request.");
}
