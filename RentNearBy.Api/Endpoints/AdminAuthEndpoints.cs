using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminAuthEndpoints
{
    public static RouteGroupBuilder MapAdminAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/login", AdminAuthHandlers.AdminLogin);
        group.MapPost("/forgot-password", AdminAuthHandlers.AdminForgotPassword);
        group.MapPost("/reset-password", AdminAuthHandlers.AdminResetPassword);
        group.MapPost("/logout", AdminAuthHandlers.AdminLogout).RequireAuthorization("AdminOnly");

        return group;
    }
}
