using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class RolesEndpoints
{
    public static RouteGroupBuilder MapRolesEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", RoleHandlers.GetRoles).RequireAuthorization();

        return group;
    }
}
