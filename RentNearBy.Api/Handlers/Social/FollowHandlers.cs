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
    public static Task<IResult> Follow(
        Guid userId, ClaimsPrincipal principal, FollowService follows, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Follow(ProfileHandlers.BuddyCaller(principal)), true,
            async me => SocialHttp.From(await follows.FollowAsync(me, userId, ct)), ct);

    public static Task<IResult> Unfollow(
        Guid userId, ClaimsPrincipal principal, FollowService follows, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me =>
            {
                await follows.UnfollowAsync(me, userId, ct);
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
