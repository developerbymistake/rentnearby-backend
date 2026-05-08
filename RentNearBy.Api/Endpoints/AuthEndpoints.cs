using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/send-otp", AuthHandlers.SendOtp).RequireRateLimiting("otp-send");
        group.MapPost("/verify-otp", AuthHandlers.VerifyOtp).RequireRateLimiting("otp-verify");
        group.MapPost("/logout", AuthHandlers.Logout).RequireAuthorization();

        return group;
    }
}
