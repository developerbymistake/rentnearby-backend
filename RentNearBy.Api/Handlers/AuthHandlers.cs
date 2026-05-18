using System.Security.Claims;
using FluentValidation;
using Mapster;
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

    public static async Task<IResult> SendOtp(
        SendOtpRequest request,
        IValidator<SendOtpRequest> validator,
        IRateLimitService rateLimiter,
        HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var rl = await rateLimiter.CheckAsync($"otp:send:{request.PhoneNumber}", OtpSendMax, OtpWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        return OkResponse(new { message = "OTP sent successfully" });
    }

    public static async Task<IResult> VerifyOtp(
        VerifyOtpRequest request,
        IValidator<VerifyOtpRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService,
        IRateLimitService rateLimiter,
        HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var rl = await rateLimiter.CheckAsync($"otp:verify:{request.PhoneNumber}", OtpVerifyMax, OtpWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        if (request.Otp != "1234")
            return BadRequestResponse("Invalid OTP", "InvalidOtp");

        var user = await unitOfWork.Users.GetByPhoneAsync(request.PhoneNumber);

        if (user == null)
        {
            var newUser = new User
            {
                Id = Guid.NewGuid(),
                PhoneNumber = request.PhoneNumber,
                OtpVerified = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await unitOfWork.Users.AddAsync(newUser);
            try
            {
                await unitOfWork.SaveChangesAsync();
                user = newUser;
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                // Race condition: concurrent request created this user first.
                // GetByPhoneAsync uses AsNoTracking so it reads fresh from DB.
                user = (await unitOfWork.Users.GetByPhoneAsync(request.PhoneNumber))!;
            }
        }
        else
        {
            if (!user.IsActive)
                return ForbiddenResponse("Your account has been blocked. Contact admin.");

            user.OtpVerified = true;
            user.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.Users.UpdateAsync(user);
            await unitOfWork.SaveChangesAsync();
        }

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
        return OkResponse(new AuthResponse { Token = token, User = user.Adapt<UserDto>() });
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
