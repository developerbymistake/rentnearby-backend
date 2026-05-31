using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/phone/send-otp", AuthHandlers.PhoneSendOtp).AllowAnonymous();
        group.MapPost("/phone/verify-otp", AuthHandlers.PhoneVerifyOtp).AllowAnonymous();
        group.MapPost("/phone/complete-onboarding", AuthHandlers.PhoneCompleteOnboarding).AllowAnonymous();
        group.MapPost("/send-otp", AuthHandlers.SendOtp).RequireAuthorization();
        group.MapPost("/verify-phone", AuthHandlers.VerifyPhone).RequireAuthorization();
        group.MapPost("/logout", AuthHandlers.Logout).RequireAuthorization();

        return group;
    }
}
