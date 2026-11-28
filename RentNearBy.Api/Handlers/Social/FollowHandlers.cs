using System.Security.Claims;
using RentNearBy.Api.Handlers.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Social;

// Contract 2.3: follow, people lists, follow requests, My Buddies.
public static class FollowHandlers
{
    // DESIGN 4.1: the Redis guard runs before any DB access, then the profile guard, then the exact DB check under the sender
    // lock. The Redis side of the outcome runs once, after PgRetry, never with the request token (an abort must still release).
    // Every limit or block answers 429 TryLater (no data, no Retry-After).
    public static async Task<IResult> Follow(
        Guid userId, ClaimsPrincipal principal, FollowService follows, FollowGuard guard, ApplicationDbContext db, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var ticket = await guard.TryAsync(db, me, userId, ct);
        if (ticket is null) return SocialHttp.Error(SocialErrorTypes.TryLater);
        FollowGuardOutcome? outcome = null;
        try
        {
            if (await SocialSql.GuardAsync(db, me, true, ct) is { } fail)
            {
                outcome = FollowGuardOutcome.Nothing;
                return SocialHttp.Error(fail.Type, fail.Data);
            }
            var written = await follows.FollowAsync(me, userId, ticket.MustBeExempt, ct);
            outcome = written.Guard;
            return SocialHttp.From(written.Result);
        }
        finally
        {
            await guard.CompleteAsync(db, ticket, outcome);
        }
    }

    // Never refused by a follow block and never refunds a follow; a deleted row feeds the churn list.
    public static Task<IResult> Unfollow(
        Guid userId, ClaimsPrincipal principal, FollowService follows, FollowGuard guard, ApplicationDbContext db, IRateLimitService limiter,
        HttpContext http, CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me =>
            {
                if (await follows.UnfollowAsync(me, userId, ct)) await guard.UnfollowedAsync(me, userId);
                return OkResponse(new { state = FollowStates.None });
            }, ct);

    public static Task<IResult> Followers(
        ClaimsPrincipal principal, FollowService follows, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? cursor = null, int? limit = null, string? q = null)
    {
        if (!BuddyHttp.TryKeyset(cursor, out var key)) return Task.FromResult(BuddyHttp.BadCursor());
        return SocialHttp.ReadAsync(principal, limiter, http, async me =>
            OkResponse(await follows.FollowersAsync(me, key, SocialSql.Clamp(limit), string.IsNullOrWhiteSpace(q) ? null : q.Trim(), ct)));
    }

    public static Task<IResult> Following(
        ClaimsPrincipal principal, FollowService follows, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? cursor = null, int? limit = null)
    {
        if (!BuddyHttp.TryKeyset(cursor, out var key)) return Task.FromResult(BuddyHttp.BadCursor());
        return SocialHttp.ReadAsync(principal, limiter, http, async me => OkResponse(await follows.FollowingAsync(me, key, SocialSql.Clamp(limit), ct)));
    }

    public static Task<IResult> RemoveFollower(
        Guid userId, ClaimsPrincipal principal, FollowService follows, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me =>
            {
                await follows.RemoveFollowerAsync(me, userId, ct);
                return SocialHttp.Empty();
            }, ct);

    public static Task<IResult> Requests(
        ClaimsPrincipal principal, FollowService follows, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? cursor = null, int? limit = null)
    {
        if (!BuddyHttp.TryKeyset(cursor, out var key)) return Task.FromResult(BuddyHttp.BadCursor());
        return SocialHttp.ReadAsync(principal, limiter, http, async me => OkResponse(await follows.RequestsAsync(me, key, SocialSql.Clamp(limit), ct)));
    }

    public static Task<IResult> AcceptRequest(
        Guid userId, ClaimsPrincipal principal, FollowService follows, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me => SocialHttp.From(await follows.AcceptRequestAsync(me, userId, ct)), ct);

    public static Task<IResult> DeclineRequest(
        Guid userId, ClaimsPrincipal principal, FollowService follows, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me =>
            {
                await follows.DeclineRequestAsync(me, userId, ct);
                return SocialHttp.Empty();
            }, ct);

    public static Task<IResult> AddBuddy(
        Guid userId, ClaimsPrincipal principal, MyBuddyService buddies, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me => SocialHttp.From(await buddies.AddAsync(me, userId, ct)), ct);
}
