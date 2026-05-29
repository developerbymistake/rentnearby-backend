using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/google", AuthHandlers.GoogleSignIn).AllowAnonymous();
        group.MapPost("/complete-onboarding", AuthHandlers.CompleteOnboarding).AllowAnonymous();
        group.MapPost("/send-otp", AuthHandlers.SendOtp).RequireAuthorization();
        group.MapPost("/verify-phone", AuthHandlers.VerifyPhone).RequireAuthorization();
        group.MapPost("/logout", AuthHandlers.Logout).RequireAuthorization();

        return group;
    }
}
