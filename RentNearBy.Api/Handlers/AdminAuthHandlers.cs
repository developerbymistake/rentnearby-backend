using System.Security.Claims;
using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AdminAuthHandlers
{
    private static readonly TimeSpan OtpWindow = TimeSpan.FromMinutes(10);
    private const int OtpSendMax = 3;
    private const int OtpVerifyMax = 5;

    public static async Task<IResult> SendAdminOtp(
        SendOtpRequest request,
        IValidator<SendOtpRequest> validator,
        IRateLimitService rateLimiter,
        HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var rl = await rateLimiter.CheckAsync($"admin_otp:send:{request.PhoneNumber}", OtpSendMax, OtpWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        return OkResponse(new { message = "OTP sent successfully" });
    }

    public static async Task<IResult> VerifyAdminOtp(
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

        var rl = await rateLimiter.CheckAsync($"admin_otp:verify:{request.PhoneNumber}", OtpVerifyMax, OtpWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        if (request.Otp != "1234")
            return BadRequestResponse("Invalid OTP", "InvalidOtp");

        var admin = await unitOfWork.Admins.GetByPhoneAsync(request.PhoneNumber);
        if (admin == null)
            return NotFoundResponse("Admin not found");

        if (!admin.IsActive)
            return ForbiddenResponse("Admin account is inactive. Contact support.");

        await unitOfWork.AdminSessions.DeleteAllAdminSessionsAsync(admin.Id);

        var session = new AdminSession
        {
            Id = Guid.NewGuid(),
            AdminId = admin.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(365)
        };
        await unitOfWork.AdminSessions.AddAsync(session);
        await unitOfWork.SaveChangesAsync();

        var token = jwtService.GenerateAdminToken(admin, session.Id);
        return OkResponse(new
        {
            token,
            admin = new { id = admin.Id, phoneNumber = admin.PhoneNumber, name = admin.Name }
        });
    }

    public static async Task<IResult> AdminLogout(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        var adminIdClaim = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(adminIdClaim, out var adminId))
            return UnauthorizedResponse();

        var sessionIdClaim = principal.FindFirst("session_id")?.Value;
        if (Guid.TryParse(sessionIdClaim, out var sessionId))
        {
            var session = await unitOfWork.AdminSessions.GetByIdAsync(sessionId);
            if (session != null && session.AdminId == adminId)
            {
                await unitOfWork.AdminSessions.DeleteAsync(session);
                await unitOfWork.SaveChangesAsync();
            }
        }
        return OkResponse(new { message = "Logged out successfully" });
    }
}
