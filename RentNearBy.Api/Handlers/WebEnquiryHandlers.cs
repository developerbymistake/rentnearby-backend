using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Public (unauthenticated) enquiry-submission flow for the marketing website — entirely separate from
// EnquiryHandlers/EnquiryEndpoints (no existing consumer-app route or handler is touched by this file).
// 3 steps, each gated by the previous one's single-use token:
//
//   1. Start        — Turnstile-verified (the one thing a scripted client can't forge) -> Token A
//   2. Confirm       — "is this your number?" -> sends OTP to the number stored server-side under
//                       Token A (never re-trusted from the client past this point) -> Token B
//   3. VerifyOtp     — OTP-verified -> finds-or-creates the User, creates the Enquiry via
//                       EnquiryHandlers.CreateEnquiryCore (same auto-assign/notify pipeline the app uses)
//
// Deliberately issues NO JWT/Session at any point — the website never receives a general-purpose access
// token, so a leaked/compromised website request can never be replayed against unrelated authenticated
// app APIs (wallet, chat, listings, ...). When this same phone number is later used to log into the
// mobile app, PhoneVerifyOtp/CreateSessionAndRespond creates its own session completely independently —
// nothing here pre-creates or interferes with that.
public static class WebEnquiryHandlers
{
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(10);

    // Per-IP limits — a second axis alongside the per-phone limits below (an attacker rotating through
    // many phone numbers from one IP would otherwise look "compliant" on every individual phone-keyed check).
    private static readonly TimeSpan IpWindow = TimeSpan.FromHours(1);
    private const int StartPerIpMax = 20;
    private const int ConfirmPerIpMax = 20;
    private const int VerifyOtpPerIpMax = 20;

    // Own OTP namespace ("web_enquiry", not "user"/"phone_login") and own rate-limit key prefix — kept
    // fully separate from AuthHandlers' login-OTP limits so this flow can never contend with or be
    // starved by a caller's own app-login OTP attempts, and vice versa.
    private const string OtpNamespace = "web_enquiry";
    private static readonly TimeSpan OtpWindow = TimeSpan.FromHours(1);
    private const int OtpSendPerPhoneMax = 2;
    private const int OtpVerifyPerPhoneMax = 3;

    public static async Task<IResult> Start(
        WebEnquiryStartRequest request, IValidator<WebEnquiryStartRequest> validator,
        IUnitOfWork unitOfWork, ITurnstileService turnstileService, IWebEnquiryTokenStore tokenStore,
        IRateLimitService rateLimiter, HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var ip = ClientIp(httpContext);
        var ipRl = await rateLimiter.CheckAsync($"web-enquiry:start:ip:{ip}", StartPerIpMax, IpWindow);
        if (!ipRl.IsAllowed) return TooManyRequestsResponse();

        // The one gate a scripted client cannot forge — see the class doc comment.
        if (!await turnstileService.VerifyAsync(request.TurnstileToken, ip))
            return BadRequestResponse("Captcha verification failed. Please try again.", "CaptchaFailed");

        var service = await unitOfWork.Services.GetByIdAsync(request.ServiceId);
        if (service == null) return NotFoundResponse("Service not found");
        if (!service.IsActive) return BadRequestResponse("This service is not currently available");

        var package = await unitOfWork.ServicePackages.GetByIdAsync(request.ServicePackageId);
        if (package == null) return NotFoundResponse("Package not found");
        if (package.ServiceId != request.ServiceId)
            return BadRequestResponse("This package does not belong to the specified service");
        if (!package.IsActive) return BadRequestResponse("This package is not currently available");

        var mobile = request.Mobile.Trim();
        var state = new WebEnquiryState
        {
            ServiceId = request.ServiceId,
            ServicePackageId = request.ServicePackageId,
            FullName = request.FullName.Trim(),
            Mobile = mobile,
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            PreferredDateOrTripStart = request.PreferredDateOrTripStart,
            NumberOfPeople = request.NumberOfPeople,
            Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
            AgreedToTerms = request.AgreedToTerms,
        };

        var token = Guid.NewGuid();
        await tokenStore.SaveAsync(TokenKey(token), JsonSerializer.Serialize(state), TokenTtl);

        return OkResponse(new { token = token.ToString(), maskedMobile = MaskMobile(mobile) });
    }

    public static async Task<IResult> Confirm(
        WebEnquiryConfirmRequest request, IValidator<WebEnquiryConfirmRequest> validator,
        IWebEnquiryTokenStore tokenStore, IOtpService otpService, IRateLimitService rateLimiter,
        HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var ip = ClientIp(httpContext);
        var ipRl = await rateLimiter.CheckAsync($"web-enquiry:confirm:ip:{ip}", ConfirmPerIpMax, IpWindow);
        if (!ipRl.IsAllowed) return TooManyRequestsResponse();

        var stateJson = await tokenStore.GetAndDeleteAsync(TokenKey(Guid.Parse(request.Token)));
        if (stateJson == null)
            return BadRequestResponse("This session has expired. Please start again.", "TokenExpiredOrInvalid");

        var state = JsonSerializer.Deserialize<WebEnquiryState>(stateJson)!;

        // Same per-phone OTP-send throttle shape as AuthHandlers.PhoneSendOtp, own namespace/keys.
        var otpRl = await rateLimiter.CheckAsync($"otp:send:{OtpNamespace}:{state.Mobile}", OtpSendPerPhoneMax, OtpWindow);
        if (!otpRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)otpRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var sent = await otpService.SendOtpAsync(state.Mobile, OtpNamespace);
        if (!sent) return Results.Problem("Could not send OTP. Please try again.", statusCode: 503);

        var nextToken = Guid.NewGuid();
        await tokenStore.SaveAsync(TokenKey(nextToken), stateJson, TokenTtl);

        return OkResponse(new { token = nextToken.ToString() });
    }

    public static async Task<IResult> VerifyOtp(
        WebEnquiryVerifyOtpRequest request, IValidator<WebEnquiryVerifyOtpRequest> validator,
        IWebEnquiryTokenStore tokenStore, IOtpService otpService, IUnitOfWork unitOfWork,
        ApplicationDbContext db, IHubContext<EnquiryHub> hubContext, IRabbitMqPublisher publisher,
        IRateLimitService rateLimiter, HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var ip = ClientIp(httpContext);
        var ipRl = await rateLimiter.CheckAsync($"web-enquiry:verify:ip:{ip}", VerifyOtpPerIpMax, IpWindow);
        if (!ipRl.IsAllowed) return TooManyRequestsResponse();

        var stateJson = await tokenStore.GetAndDeleteAsync(TokenKey(Guid.Parse(request.Token)));
        if (stateJson == null)
            return BadRequestResponse("This session has expired. Please start again.", "TokenExpiredOrInvalid");

        var state = JsonSerializer.Deserialize<WebEnquiryState>(stateJson)!;

        var verifyRl = await rateLimiter.CheckAsync($"otp:verify:{OtpNamespace}:{state.Mobile}", OtpVerifyPerPhoneMax, OtpWindow);
        if (!verifyRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)verifyRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        if (!await otpService.VerifyOtpAsync(state.Mobile, request.Otp, OtpNamespace))
            return BadRequestResponse("Invalid OTP", "InvalidOtp");

        // Existing-user branch — never create a duplicate account for a number that's already
        // registered (matches AuthHandlers.PhoneVerifyOtp's own GetByVerifiedPhoneAsync check exactly).
        var user = await unitOfWork.Users.GetByVerifiedPhoneAsync(state.Mobile);
        if (user != null)
        {
            if (!user.IsActive) return ForbiddenResponse("This account has been blocked. Contact admin.");
        }
        else
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                PhoneNumber = state.Mobile,
                Name = state.FullName,
                IsPhoneVerified = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await unitOfWork.Users.AddAsync(user);
            // Saved immediately (mirrors AuthHandlers.PhoneCompleteOnboarding's own separate save for a
            // new user) rather than left pending alongside the Enquiry's own SaveChangesAsync below —
            // keeps user-creation correct regardless of whether Enquiry.UserId carries a real DB-level FK.
            await unitOfWork.SaveChangesAsync();
        }

        // Same per-user/per-target-mobile caps as the authenticated app path — see
        // EnquiryHandlers.CreateEnquiry, reused verbatim so a website-originated enquiry is bound by the
        // identical abuse limits, not a separate/weaker set.
        var userRl = await rateLimiter.CheckAsync(
            $"enquiry:create:{user.Id}", EnquiryHandlers.EnquiryCreatePerUserMax, EnquiryHandlers.EnquiryCreateWindow);
        if (!userRl.IsAllowed) return TooManyRequestsResponse();

        var mobileRl = await rateLimiter.CheckAsync(
            $"enquiry:create:mobile:{state.Mobile}", EnquiryHandlers.EnquiryCreatePerMobileMax, EnquiryHandlers.EnquiryCreateWindow);
        if (!mobileRl.IsAllowed) return TooManyRequestsResponse();

        var createRequest = new CreateEnquiryRequest(
            state.ServiceId, state.ServicePackageId, state.FullName, state.Mobile, state.Email,
            state.PreferredDateOrTripStart, state.NumberOfPeople, state.Message, state.AgreedToTerms);

        return await EnquiryHandlers.CreateEnquiryCore(user.Id, createRequest, unitOfWork, db, hubContext, publisher);
    }

    // Program.cs now runs UseForwardedHeaders() ahead of everything else, so RemoteIpAddress here is
    // already resolved from the (Coolify/Traefik) reverse proxy's X-Forwarded-For header, not the
    // proxy's own container IP — see the ForwardedHeadersOptions comment in Program.cs for the trust
    // caveat this depends on (this container must not be reachable directly, bypassing the proxy).
    private static string ClientIp(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string TokenKey(Guid token) => $"web-enquiry:token:{token}";

    // Keeps the mobile number's last 4 digits visible (so the user can actually recognize/confirm it)
    // and masks the rest — shown back on the "is this your number?" confirmation step.
    private static string MaskMobile(string mobile) =>
        mobile.Length <= 4 ? mobile : new string('•', mobile.Length - 4) + mobile[^4..];

    // Internal wire shape for the token store's JSON blob — never serialized over the public API itself,
    // purely an implementation detail carried token-to-token between the 3 steps.
    private class WebEnquiryState
    {
        public Guid ServiceId { get; set; }
        public Guid ServicePackageId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Mobile { get; set; } = string.Empty;
        public string? Email { get; set; }
        public DateTime? PreferredDateOrTripStart { get; set; }
        public int? NumberOfPeople { get; set; }
        public string? Message { get; set; }
        public bool AgreedToTerms { get; set; }
    }
}
