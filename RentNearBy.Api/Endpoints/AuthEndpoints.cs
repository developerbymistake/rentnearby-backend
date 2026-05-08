using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/send-otp", AuthHandlers.SendOtp);
        group.MapPost("/verify-otp", AuthHandlers.VerifyOtp);
        group.MapPost("/logout", AuthHandlers.Logout).RequireAuthorization();

        return group;
    }
}
