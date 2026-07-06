using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder group)
    {
        group.MapDelete("/", AccountHandlers.DeleteAccount).RequireAuthorization();

        return group;
    }
}
