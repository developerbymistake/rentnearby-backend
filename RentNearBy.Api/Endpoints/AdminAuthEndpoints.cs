using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminAuthEndpoints
{
    public static RouteGroupBuilder MapAdminAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/login", AdminAuthHandlers.AdminLogin);
        group.MapPost("/send-reset-otp", AdminAuthHandlers.AdminSendResetOtp);
        group.MapPost("/reset-password", AdminAuthHandlers.AdminResetPassword);
        group.MapPost("/logout", AdminAuthHandlers.AdminLogout).RequireAuthorization("AdminOnly");

        group.MapPost("/register-token", AdminNotificationHandlers.RegisterToken).RequireAuthorization("AdminOnly");
        group.MapDelete("/register-token", AdminNotificationHandlers.UnregisterToken).RequireAuthorization("AdminOnly");

        return group;
    }
}
