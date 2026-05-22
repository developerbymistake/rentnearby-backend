using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminAuthEndpoints
{
    public static RouteGroupBuilder MapAdminAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/send-otp", AdminAuthHandlers.SendAdminOtp);
        group.MapPost("/verify-otp", AdminAuthHandlers.VerifyAdminOtp);
        group.MapPost("/logout", AdminAuthHandlers.AdminLogout).RequireAuthorization("AdminOnly");

        return group;
    }
}
