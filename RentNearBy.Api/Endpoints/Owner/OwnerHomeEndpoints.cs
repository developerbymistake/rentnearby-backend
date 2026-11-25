using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers.Owner.Be4;

namespace RentNearBy.Api.Endpoints.Owner;

// Lane BE4 routes: H1 owner Home (ungated, contract 2.0) and the admin plan key routes (AdminOnly). H2 lives in HomeEndpoints.
public static class OwnerHomeEndpoints
{
    public static RouteGroupBuilder MapOwnerHomeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/home", OwnerHomeHandlers.Get).ExpiresPlanOnRead();
        return group;
    }

    public static RouteGroupBuilder MapAdminOwnerPlanEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/plans/keys", AdminPlanKeyHandlers.GetKeys);
        group.MapGet("/plans/{id:guid}", AdminPlanKeyHandlers.GetPlanValues);
        group.MapGet("/reports/special-prices", AdminPlanKeyHandlers.GetSpecialPriceReport);
        return group;
    }
}
