using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class UsersEndpoints
{
    public static RouteGroupBuilder MapUsersEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/profile", UsersHandlers.GetProfile).RequireAuthorization();
        group.MapPut("/profile", UsersHandlers.UpdateProfile).RequireAuthorization();
        group.MapGet("/reports", UsersHandlers.GetMyReports).RequireAuthorization();

        return group;
    }
}
