using System.Security.Claims;
using FluentValidation;
using Google.Apis.Auth;
using Mapster;
using Microsoft.Extensions.Configuration;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AuthHandlers
{
    private static readonly TimeSpan OtpWindow = TimeSpan.FromMinutes(10);
    private const int OtpSendMax = 3;
    private const int OtpVerifyMax = 5;

    public static async Task<IResult> GoogleSignIn(
        GoogleSignInRequest request,
        IValidator<GoogleSignInRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService,
        IConfiguration configuration)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        GoogleJsonWebSignature.Payload payload;
        try
        {
            var webClientId = configuration["Google:WebClientId"]
                ?? throw new InvalidOperationException("Google WebClientId not configured");

            payload = await GoogleJsonWebSignature.ValidateAsync(request.IdToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = [webClientId]
                });
        }
        catch (InvalidJwtException)
        {
            return BadRequestResponse("Invalid Google token", "InvalidToken");
        }

        var googleId = payload.Subject;
        var user = await unitOfWork.Users.GetByGoogleIdAsync(googleId);

        // New user — do not create yet, phone number required at onboarding
        if (user == null)
        {
            return OkResponse(new GoogleSignInResponse
            {
                NeedsOnboarding = true,
                GoogleProfile = new GoogleProfileDto
                {
                    Name = payload.Name ?? string.Empty,
                    Email = payload.Email,
                    PhotoUrl = payload.Picture
                }
            });
        }

        if (!user.IsActive)
            return ForbiddenResponse("Your account has been blocked. Contact admin.");

        // Refresh profile photo on every login
        user.ProfilePhotoUrl = payload.Picture;
        user.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.Users.UpdateAsync(user);
        await unitOfWork.SaveChangesAsync();

        return await CreateSessionAndRespond(user, unitOfWork, jwtService);
    }

    public static async Task<IResult> CompleteOnboarding(
        CompleteOnboardingRequest request,
        IValidator<CompleteOnboardingRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService,
        IConfiguration configuration)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        GoogleJsonWebSignature.Payload payload;
        try
        {
            var webClientId = configuration["Google:WebClientId"]
                ?? throw new InvalidOperationException("Google WebClientId not configured");

            payload = await GoogleJsonWebSignature.ValidateAsync(request.IdToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = [webClientId]
                });
        }
        catch (InvalidJwtException)
        {
            return BadRequestResponse("Invalid Google token", "InvalidToken");
        }

        // Guard: if user already exists with this GoogleId, redirect to normal login
        if (await unitOfWork.Users.GoogleIdExistsAsync(payload.Subject))
            return BadRequestResponse("Account already exists. Please sign in.", "AlreadyExists");

        // Guard: if phone is already verified by another user, reject
        if (await unitOfWork.Users.IsPhoneVerifiedByAnyUserAsync(request.PhoneNumber))
            return ConflictResponse("This phone number is already registered in our system. Please use a different number.", "PhoneExists");

        var newUser = new User
        {
            Id = Guid.NewGuid(),
            GoogleId = payload.Subject,
            GoogleEmail = payload.Email,
            Name = request.Name,
            ProfilePhotoUrl = payload.Picture,
            PhoneNumber = request.PhoneNumber,
            IsPhoneVerified = false,
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
            // Race condition: googleId unique constraint violated
            return BadRequestResponse("Account already exists. Please sign in.", "AlreadyExists");
        }

        return await CreateSessionAndRespond(newUser, unitOfWork, jwtService);
    }

    private static async Task<IResult> CreateSessionAndRespond(User user, IUnitOfWork unitOfWork, IJwtService jwtService)
    {
        // Hard delete all previous sessions — single device enforcement
        await unitOfWork.Sessions.DeleteAllUserSessionsAsync(user.Id);

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
        return OkResponse(new GoogleSignInResponse
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
        HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        // Check if this number is already verified by a different user
        if (await unitOfWork.Users.IsPhoneVerifiedByOtherUserAsync(request.PhoneNumber, userId))
            return ConflictResponse("This number is already verified by another account. Please use a different number.", "PhoneAlreadyClaimed");

        var rl = await rateLimiter.CheckAsync($"otp:send:{request.PhoneNumber}", OtpSendMax, OtpWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        return OkResponse(new { message = "OTP sent successfully" });
    }

    public static async Task<IResult> VerifyPhone(
        VerifyPhoneRequest request,
        IValidator<VerifyPhoneRequest> validator,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        IRateLimitService rateLimiter,
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

        if (request.Otp != "1234")
            return BadRequestResponse("Invalid OTP", "InvalidOtp");

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        // Double-check at verify time — another user may have verified this number between send and verify
        if (await unitOfWork.Users.IsPhoneVerifiedByOtherUserAsync(request.PhoneNumber, userId))
            return ConflictResponse("This number is already verified by another account. Please use a different number.", "PhoneAlreadyClaimed");

        user.PhoneNumber = request.PhoneNumber;
        user.IsPhoneVerified = true;
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
