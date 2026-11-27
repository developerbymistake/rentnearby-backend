using System.Security.Claims;
using FluentValidation;
using RentNearBy.Api.Handlers.Buddy;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Social;

// Contract 2.4: feed, rail, post page, create / publish / edit / unpublish.
public static class PublicTripHandlers
{
    public static async Task<IResult> Feed(
        ClaimsPrincipal principal, PublicTripReader reader, ISeekerLimits seekerLimits, IRateLimitService limiter, HttpContext http,
        CancellationToken ct, string? cursor = null, int? limit = null, string? q = null, string? who = null, bool near = false,
        double? lat = null, double? lng = null, string? travel = null)
    {
        if (!BuddyHttp.TryKeyset(cursor, out var key)) return BuddyHttp.BadCursor();
        var query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (query is { Length: < SocialLimits.FeedQueryMin or > SocialLimits.FeedQueryMax }) return BadRequestResponse("Search between 2 and 40 characters.");
        if (!JoinGenders.TryWhoFilter(who, out var whoJoinGender)) return BadRequestResponse("Who must be any, anyone, women or men.");
        if (!PostTravelModes.TryFilter(travel, out var travelMode)) return BadRequestResponse("Travel must be any, public, bike, car or taxi.");
        if (near && (lat is not (>= -90 and <= 90) || lng is not (>= -180 and <= 180))) return BadRequestResponse("Location is required.");
        return await SocialHttp.ReadAsync(principal, limiter, http, async me =>
        {
            if (near && !(await seekerLimits.GetAsync(me, ct)).NearMe) return SocialHttp.Error(SocialErrorTypes.PlusRequired);
            return OkResponse(await reader.FeedAsync(me, key, SocialSql.Clamp(limit), query, whoJoinGender,
                near ? (lat!.Value, lng!.Value) : null, travelMode, ct));
        });
    }

    public static Task<IResult> Rail(ClaimsPrincipal principal, PublicTripReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.ReadAsync(principal, limiter, http, async me => OkResponse(await reader.RailAsync(me, ct)));

    public static Task<IResult> Get(
        Guid tripId, ClaimsPrincipal principal, PublicTripReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.ReadAsync(principal, limiter, http, async me =>
            await reader.ByIdAsync(me, tripId, ct) is { } dto ? OkResponse(dto) : SocialHttp.Error(SocialErrorTypes.TripPostNotFound));

    public static async Task<IResult> Create(
        CreatePublicTripRequest request, IValidator<CreatePublicTripRequest> validator, ClaimsPrincipal principal, PublicTripService posts,
        ApplicationDbContext db, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        return await SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Publish(ProfileHandlers.BuddyCaller(principal)), true,
            async me => SocialHttp.From(await posts.CreateAsync(me, request, ct)), ct);
    }

    public static async Task<IResult> PutPost(
        Guid tripId, UpdatePublicPostRequest request, IValidator<UpdatePublicPostRequest> validator, ClaimsPrincipal principal,
        PublicTripService posts, ApplicationDbContext db, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        return await SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Publish(ProfileHandlers.BuddyCaller(principal)), true,
            async me => SocialHttp.From(await posts.PutPostAsync(me, tripId, request, ct)), ct);
    }

    public static Task<IResult> DeletePost(
        Guid tripId, ClaimsPrincipal principal, PublicTripService posts, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me => await posts.UnpublishAsync(me, tripId, ct) is { Ok: false } refused
                ? SocialHttp.Error(refused.ErrorType!, refused.ErrorData)
                : SocialHttp.Empty(), ct);
}
