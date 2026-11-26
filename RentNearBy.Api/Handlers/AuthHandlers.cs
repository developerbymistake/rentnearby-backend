using System.Security.Claims;
using FluentValidation;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AuthHandlers
{
    private static readonly TimeSpan OtpWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan OtpUserDailyWindow = TimeSpan.FromHours(4);
    private const int OtpSendMax = 2;
    private const int OtpUserDailySendMax = 2;
    private const int OtpVerifyMax = 3;
    private const string PhoneLoginNamespace = "phone_login";
    public const int OnboardPhoneMax = 5;
    public const int OnboardIpMax = 30;
    private static readonly TimeSpan OnboardWindow = TimeSpan.FromHours(1);

    public static async Task<IResult> PhoneSendOtp(
        PhoneLoginSendOtpRequest request,
        IValidator<PhoneLoginSendOtpRequest> validator,
        IRateLimitService rateLimiter,
        IOtpService otpService,
        HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        // Per-phone limit: 2 per hour
        var sends = new RateLimitPermits(rateLimiter);
        var phoneLimitRl = await sends.TakeAsync($"otp:send:login:{request.PhoneNumber}", OtpSendMax, OtpWindow);
        if (!phoneLimitRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)phoneLimitRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var sent = await otpService.SendOtpAsync(request.PhoneNumber, PhoneLoginNamespace);
        if (!sent)
        {
            await sends.ReleaseAllAsync();
            return Results.Problem("Could not send OTP. Please try again.", statusCode: 503);
        }

        return OkResponse(new { message = "OTP sent successfully" });
    }

    public static async Task<IResult> PhoneVerifyOtp(
        PhoneLoginVerifyOtpRequest request,
        IValidator<PhoneLoginVerifyOtpRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService,
        IRateLimitService rateLimiter,
        IOtpService otpService,
        HttpContext httpContext,
        IMemoryCache cache,
        IUsernameService usernames)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        // Verify rate limit per phone
        var rl = await rateLimiter.CheckAsync($"otp:verify:login:{request.PhoneNumber}", OtpVerifyMax, OtpWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        if (!await otpService.VerifyOtpAsync(request.PhoneNumber, request.Otp, PhoneLoginNamespace))
            return BadRequestResponse("Invalid OTP", "InvalidOtp");

        // Check if phone is already registered
        var existingUser = await unitOfWork.Users.GetByVerifiedPhoneAsync(request.PhoneNumber);
        if (existingUser != null)
        {
            if (!existingUser.IsActive)
                return ForbiddenResponse("Your account has been blocked. Contact admin.");

            return await CreateSessionAndRespond(existingUser, unitOfWork, jwtService, cache, usernames);
        }

        // New user — needs onboarding
        return OkResponse(new PhoneLoginResponse
        {
            NeedsOnboarding = true,
            Username = await usernames.ReserveForSignupAsync(request.PhoneNumber)
        });
    }

    public static async Task<IResult> PhoneCompleteOnboarding(
        PhoneOnboardingRequest request,
        IValidator<PhoneOnboardingRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService,
        IMemoryCache cache,
        IUsernameService usernames,
        IRateLimitService rateLimiter,
        HttpContext httpContext,
        IConfiguration configuration,
        [Microsoft.AspNetCore.Mvc.FromServices] RentNearBy.Infrastructure.Data.ApplicationDbContext? db = null)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        // Bakhli social (contract 2.2): the optional profile block is checked before anything is stored; under 18 = no account.
        var social = new RentNearBy.Core.DTOs.Social.SocialOnboardingRequest
        {
            Gender = request.Gender, BirthMonth = request.BirthMonth, BirthYear = request.BirthYear, Interests = request.Interests,
            Skills = request.Skills, Places = request.Places, About = request.About,
            HomeCityName = request.HomeCityName, HomeStateName = request.HomeStateName,
        };
        RentNearBy.Core.Entities.Social.UserProfile? profile = null;
        if (SocialProfileService.HasOnboarding(social))
        {
            var built = SocialProfileService.Build(Guid.Empty, social, IstTime.TodayIst(DateTime.UtcNow));
            if (built.Profile is null) return RentNearBy.Api.Handlers.Social.SocialHttp.Error(built.ErrorType!, built.ErrorData);
            profile = built.Profile;
        }

        // Proof before the registered check, so the route cannot probe which numbers exist.
        var proof = await usernames.ClassifySignupAsync(request.PhoneNumber, request.Username);

        // Limits apply to calls without the bound proof.
        if (proof != SignupProof.Bound)
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            foreach (var (key, max) in new[] { ($"onboard:{request.PhoneNumber}", OnboardPhoneMax), ($"onboard:ip:{ip}", OnboardIpMax) })
            {
                var limit = await rateLimiter.CheckAsync(key, max, OnboardWindow);
                if (!limit.IsAllowed)
                {
                    httpContext.Response.Headers["Retry-After"] = ((int)Math.Ceiling(limit.RetryAfter!.Value.TotalSeconds)).ToString();
                    return TooManyRequestsResponse();
                }
            }

            if (proof == SignupProof.None || !configuration.GetValue("Auth:AllowOnboardingWithoutProof", false))
                return BadRequestResponse("Please verify your phone number again.", "OtpRequired");
        }

        // Guard: phone already registered
        if (await unitOfWork.Users.IsPhoneVerifiedByAnyUserAsync(request.PhoneNumber))
            return ConflictResponse("This phone number is already registered. Please sign in.", "PhoneExists");

        var newUser = new User
        {
            Id = Guid.NewGuid(),
            PhoneNumber = request.PhoneNumber,
            Name = request.Name,
            IsPhoneVerified = true,
            IsContactVisible = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        if (profile is not null)
        {
            if (db is null) throw new InvalidOperationException("The database context is required to store the social profile.");
            // Saved with the user by the same SaveChanges (one transaction, retried with it on a username clash).
            profile.UserId = newUser.Id;
            profile.CompletedAt = newUser.CreatedAt;
            profile.UpdatedAt = newUser.CreatedAt;
            db.Set<RentNearBy.Core.Entities.Social.UserProfile>().Add(profile);
        }
        try
        {
            await usernames.SaveNewUserAsync(newUser);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return ConflictResponse("This phone number is already registered. Please sign in.", "PhoneExists");
        }

        return await CreateSessionAndRespond(newUser, unitOfWork, jwtService, cache, usernames);
    }

    private static async Task<IResult> CreateSessionAndRespond(User user, IUnitOfWork unitOfWork, IJwtService jwtService, IMemoryCache cache, IUsernameService usernames)
    {
        await unitOfWork.Sessions.DeleteAllUserSessionsAsync(user.Id);
        await unitOfWork.DeviceTokens.DeleteByUserIdAsync(user.Id);

        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(365)
        };
        await unitOfWork.Sessions.AddAsync(session);
        await unitOfWork.SaveChangesAsync();

        var token = jwtService.GenerateToken(user, session.Id);
        var dto = await UsersHandlers.BuildUserDtoAsync(user, unitOfWork, cache, usernames);
        return OkResponse(new PhoneLoginResponse
        {
            NeedsOnboarding = false,
            Token = token,
            Username = dto.Username,
            User = dto
        });
    }

    public static async Task<IResult> SendOtp(
        SendOtpRequest request,
        IValidator<SendOtpRequest> validator,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        IRateLimitService rateLimiter,
        IOtpService otpService,
        HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var currentUser = await unitOfWork.Users.GetByIdAsync(userId);
        if (currentUser == null) return NotFoundResponse("User not found");

        if (currentUser.IsPhoneVerified && currentUser.HasUsedPhoneChange)
            return ForbiddenResponse("Phone number change is no longer allowed.");

        if (await unitOfWork.Users.IsPhoneUsedByOtherUserAsync(request.PhoneNumber, userId))
            return ConflictResponse("This number is already registered to another account. Please use a different number.", "PhoneAlreadyClaimed");

        var sends = new RateLimitPermits(rateLimiter);
        var phoneLimitRl = await sends.TakeAsync($"otp:send:{request.PhoneNumber}", OtpSendMax, OtpWindow);
        if (!phoneLimitRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)phoneLimitRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var userDailyRl = await sends.TakeAsync($"otp:send:user:{userId}", OtpUserDailySendMax, OtpUserDailyWindow);
        if (!userDailyRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)userDailyRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var sent = await otpService.SendOtpAsync(request.PhoneNumber);
        if (!sent)
        {
            await sends.ReleaseAllAsync();
            return Results.Problem("Could not send OTP. Please try again.", statusCode: 503);
        }

        return OkResponse(new { message = "OTP sent successfully" });
    }

    public static async Task<IResult> VerifyPhone(
        VerifyPhoneRequest request,
        IValidator<VerifyPhoneRequest> validator,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        IRateLimitService rateLimiter,
        IOtpService otpService,
        HttpContext httpContext,
        IMemoryCache cache,
        IUsernameService usernames)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"phone:verify:{userId}", OtpVerifyMax, OtpWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        if (!await otpService.VerifyOtpAsync(request.PhoneNumber, request.Otp))
            return BadRequestResponse("Invalid OTP", "InvalidOtp");

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        if (await unitOfWork.Users.IsPhoneUsedByOtherUserAsync(request.PhoneNumber, userId))
            return ConflictResponse("This number is already registered to another account. Please use a different number.", "PhoneAlreadyClaimed");

        bool wasAlreadyVerified = user.IsPhoneVerified;

        user.PhoneNumber = request.PhoneNumber;
        user.IsPhoneVerified = true;
        if (wasAlreadyVerified) user.HasUsedPhoneChange = true;
        user.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Users.UpdateAsync(user);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return ConflictResponse("This number is already registered to another account. Please use a different number.", "PhoneAlreadyClaimed");
        }

        return OkResponse(await UsersHandlers.BuildUserDtoAsync(user, unitOfWork, cache, usernames));
    }

    public static async Task<IResult> Logout(ClaimsPrincipal principal, IUnitOfWork unitOfWork, Infrastructure.Data.ApplicationDbContext db,
        [Microsoft.AspNetCore.Mvc.FromServices] RentNearBy.Core.Interfaces.Social.IHubSessionRevoker? hubSessions = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        // Owner contract 6.1: one session per user, so logout also deletes every device token of the user in the same transaction
        // as the session revoke. A forced logout whose unregister call never reached the server still stops every push.
        await unitOfWork.BeginTransactionAsync();
        try
        {
            var sessionIdClaim = principal.FindFirst("session_id")?.Value;
            if (Guid.TryParse(sessionIdClaim, out var sessionId))
            {
                var session = await unitOfWork.Sessions.GetByIdAsync(sessionId);
                if (session != null && session.UserId == userId)
                {
                    // Soft-revoke (not delete) so the row survives as the user's last-known session
                    // for support/audit purposes — DeleteAllUserSessionsAsync on the next login still
                    // wipes it, so this never accumulates beyond one row per user (see CreateSessionAndRespond).
                    session.IsActive = false;
                    session.LoggedOutAt = DateTime.UtcNow;
                    await unitOfWork.Sessions.UpdateAsync(session);
                    await unitOfWork.SaveChangesAsync();
                }
            }
            await DeviceTokenCleanup.DeleteAllAsync(db, userId);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        // Social chat sockets of this account end after the commit (contract 3.1).
        if (hubSessions != null) await hubSessions.RevokeAsync(userId, "loggedOut");
        return OkResponse(new { message = "Logged out successfully" });
    }
}
