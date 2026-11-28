using System.Security.Claims;
using RentNearBy.Api.Handlers.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Social;

// Discover travellers (Buddy_resume/discover/DESIGN.md 1). Chip and cursor are checked before the caller's read permit; the Near
// you chip needs Plus (SeekerLimits.NearMe), while Free users still get near-you people inside For you.
public static class DiscoverHandlers
{
    public static async Task<IResult> Discover(
        ClaimsPrincipal principal, IDiscoverService discover, ISeekerLimits seekerLimits, IRateLimitService limiter, HttpContext http,
        CancellationToken ct, string? chip = null, string? cursor = null, int? limit = null, string? refresh = null)
    {
        if (!DiscoverChips.TryParse(chip, out var picked)) return BadRequestResponse(DiscoverChips.BadChipMessage);
        DiscoverCursorValue? key = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!DiscoverCursor.TryDecode(cursor, out var value)) return BuddyHttp.BadCursor();
            key = value;
        }
        var take = BuddyHttp.ClampLimit(limit, SocialLimits.DiscoverPage, SocialLimits.DiscoverPage);
        return await SocialHttp.ReadAsync(principal, limiter, http, async me =>
        {
            if (picked == DiscoverChips.Near && !(await seekerLimits.GetAsync(me, ct)).NearMe) return SocialHttp.Error(SocialErrorTypes.PlusRequired);
            return OkResponse(await discover.PageAsync(me, picked, key, take, refresh == "1", ct));
        });
    }

    public static Task<IResult> Hide(
        Guid userId, ClaimsPrincipal principal, IDiscoverService discover, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me =>
            {
                await discover.HideAsync(me, userId, ct);
                return SocialHttp.Empty();
            }, ct);

    public static Task<IResult> Similar(
        Guid userId, ClaimsPrincipal principal, IDiscoverService discover, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        int? limit = null)
        => SocialHttp.ReadAsync(principal, limiter, http, async me =>
            OkResponse(await discover.SimilarAsync(me, userId,
                BuddyHttp.ClampLimit(limit, SocialLimits.DiscoverSimilarDefault, SocialLimits.DiscoverSimilarMax), ct)));
}
