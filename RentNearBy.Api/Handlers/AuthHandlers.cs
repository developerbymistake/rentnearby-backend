using System.Security.Claims;
using FluentValidation;
using Mapster;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
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
        var phoneLimitRl = await rateLimiter.CheckAsync($"otp:send:login:{request.PhoneNumber}", OtpSendMax, OtpWindow);
        if (!phoneLimitRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)phoneLimitRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var sent = await otpService.SendOtpAsync(request.PhoneNumber, PhoneLoginNamespace);
        if (!sent)
            return Results.Problem("Could not send OTP. Please try again.", statusCode: 503);

        return OkResponse(new { message = "OTP sent successfully" });
    }

    public static async Task<IResult> PhoneVerifyOtp(
        PhoneLoginVerifyOtpRequest request,
        IValidator<PhoneLoginVerifyOtpRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService,
        IRateLimitService rateLimiter,
        IOtpService otpService,
        HttpContext httpContext)
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

            return await CreateSessionAndRespond(existingUser, unitOfWork, jwtService);
        }

        // New user — needs onboarding
        return OkResponse(new PhoneLoginResponse { NeedsOnboarding = true });
    }

    public static async Task<IResult> PhoneCompleteOnboarding(
        PhoneOnboardingRequest request,
        IValidator<PhoneOnboardingRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService,
        ICouponService couponService,
        ILogger<CouponService> logger)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        // Guard: phone already registered
        if (await unitOfWork.Users.IsPhoneVerifiedByAnyUserAsync(request.PhoneNumber))
            return ConflictResponse("This phone number is already registered. Please sign in.", "PhoneExists");

        var newUser = new User
        {
            Id = Guid.NewGuid(),
            PhoneNumber = request.PhoneNumber,
            Name = request.Name,
            IsPhoneVerified = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await unitOfWork.Users.AddAsync(newUser);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return ConflictResponse("This phone number is already registered. Please sign in.", "PhoneExists");
        }

        // Best-effort — a welcome-bonus hiccup must never block signup itself. The redemption's own
        // (CouponId, UserId) unique index also protects against a retried signup double-crediting.
        try
        {
            await couponService.RedeemCouponAsync(newUser.Id, WellKnownCoupons.WelcomeSignupCouponId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Welcome bonus redemption failed for new user {UserId} — continuing signup", newUser.Id);
        }

        return await CreateSessionAndRespond(newUser, unitOfWork, jwtService);
    }

    private static async Task<IResult> CreateSessionAndRespond(User user, IUnitOfWork unitOfWork, IJwtService jwtService)
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
        return OkResponse(new PhoneLoginResponse
        {
            NeedsOnboarding = false,
            Token = token,
            User = user.Adapt<UserDto>()
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

        if (await unitOfWork.Users.IsPhoneVerifiedByOtherUserAsync(request.PhoneNumber, userId))
            return ConflictResponse("This number is already verified by another account. Please use a different number.", "PhoneAlreadyClaimed");

        var phoneLimitRl = await rateLimiter.CheckAsync($"otp:send:{request.PhoneNumber}", OtpSendMax, OtpWindow);
        if (!phoneLimitRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)phoneLimitRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var userDailyRl = await rateLimiter.CheckAsync($"otp:send:user:{userId}", OtpUserDailySendMax, OtpUserDailyWindow);
        if (!userDailyRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)userDailyRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var sent = await otpService.SendOtpAsync(request.PhoneNumber);
        if (!sent)
            return Results.Problem("Could not send OTP. Please try again.", statusCode: 503);

        return OkResponse(new { message = "OTP sent successfully" });
    }

    public static async Task<IResult> VerifyPhone(
        VerifyPhoneRequest request,
        IValidator<VerifyPhoneRequest> validator,
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

        if (await unitOfWork.Users.IsPhoneVerifiedByOtherUserAsync(request.PhoneNumber, userId))
            return ConflictResponse("This number is already verified by another account. Please use a different number.", "PhoneAlreadyClaimed");

        bool wasAlreadyVerified = user.IsPhoneVerified;

        user.PhoneNumber = request.PhoneNumber;
        user.IsPhoneVerified = true;
        if (wasAlreadyVerified) user.HasUsedPhoneChange = true;
        user.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Users.UpdateAsync(user);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(user.Adapt<UserDto>());
    }

    public static async Task<IResult> Logout(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var sessionIdClaim = principal.FindFirst("session_id")?.Value;
        if (Guid.TryParse(sessionIdClaim, out var sessionId))
        {
            var session = await unitOfWork.Sessions.GetByIdAsync(sessionId);
            if (session != null && session.UserId == userId)
            {
                await unitOfWork.Sessions.DeleteAsync(session);
                await unitOfWork.SaveChangesAsync();
            }
        }
        return OkResponse(new { message = "Logged out successfully" });
    }
}
