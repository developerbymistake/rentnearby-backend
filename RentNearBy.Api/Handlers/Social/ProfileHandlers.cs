using System.Security.Claims;
using FluentValidation;
using RentNearBy.Api.Handlers.Buddy;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Social;

// Contract 2.2: GET /social/me, onboarding, profile, privacy, traveller profile.
public static class ProfileHandlers
{
    public static Task<IResult> GetMe(ClaimsPrincipal principal, SocialProfileService profiles, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.ReadAsync(principal, limiter, http, async me => OkResponse(await profiles.GetMeAsync(me, ct)));

    // Any role (existing accounts finishing the 3 steps); written once.
    public static async Task<IResult> Onboard(
        SocialOnboardingRequest request, IValidator<SocialOnboardingRequest> validator, ClaimsPrincipal principal,
        SocialProfileService profiles, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        return await BuddyHttp.ActionLimitAsync(limiter, http, SocialRateLimits.ProfileEdit(me),
            async () => SocialHttp.From(await profiles.OnboardAsync(me, request, ct)));
    }

    public static async Task<IResult> UpdateProfile(
        UpdateSocialProfileRequest request, IValidator<UpdateSocialProfileRequest> validator, ClaimsPrincipal principal,
        SocialProfileService profiles, ApplicationDbContext db, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        return await SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.ProfileEdit(BuddyCaller(principal)), false,
            async me => SocialHttp.From(await profiles.UpdateProfileAsync(me, request, ct)), ct);
    }

    public static async Task<IResult> UpdatePrivacy(
        UpdatePrivacyRequest request, IValidator<UpdatePrivacyRequest> validator, ClaimsPrincipal principal,
        SocialProfileService profiles, ApplicationDbContext db, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        return await SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(BuddyCaller(principal)), false,
            async me => SocialHttp.From(await profiles.UpdatePrivacyAsync(me, request, ct)), ct);
    }

    // Owner 2026-11-27: PUT /social/me/area, the district the app is in (id only). Throttled per user; no social hold (it touches
    // nobody). The app ignores every error (the nearby digest simply skips a stale area).
    public static async Task<IResult> UpdateArea(
        UpdateAreaRequest request, IValidator<UpdateAreaRequest> validator, ClaimsPrincipal principal,
        SocialProfileService profiles, ApplicationDbContext db, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Error(SocialErrorTypes.InvalidArea, new { field = "districtId" });
        return await SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Area(BuddyCaller(principal)), false,
            async me => SocialHttp.From(await profiles.SetAreaAsync(me, request.DistrictId, ct)), ct);
    }

    // Spec 25: accept the community guidelines (idempotent).
    public static Task<IResult> AcceptGuidelines(
        ClaimsPrincipal principal, SocialProfileService profiles, ApplicationDbContext db, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(BuddyCaller(principal)), false,
            async me =>
            {
                var result = await profiles.AcceptGuidelinesAsync(me, ct);
                return result.Ok ? OkResponse(new { guidelinesAccepted = true }) : SocialHttp.Error(result.ErrorType!, result.ErrorData);
            }, ct);

    public static Task<IResult> GetTraveller(
        Guid userId, ClaimsPrincipal principal, SocialProfileService profiles, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.ReadAsync(principal, limiter, http, async me =>
            await profiles.TravellerAsync(me, userId, ct) is { } dto ? OkResponse(dto) : SocialHttp.Error(SocialErrorTypes.PersonNotFound));

    // The rate-limit key needs the caller id before the wrapper runs; an unauthenticated call is refused by the wrapper.
    internal static Guid BuddyCaller(ClaimsPrincipal principal) => BuddyHttp.TryCaller(principal, out var me) ? me : Guid.Empty;
}
