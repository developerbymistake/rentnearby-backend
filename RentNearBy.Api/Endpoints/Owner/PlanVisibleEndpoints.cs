using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Handlers.Owner.Be3;

namespace RentNearBy.Api.Endpoints.Owner;

// Lane BE3 owner routes, in ungated groups (no TabGateFilter): the hub reads M1 / M2 (R-5: the owner's own row stays readable
// while the public tab is off) and choose what stays V1 / V2. Every other /stays/me and /taxis/me write keeps its gate.
public static class PlanVisibleEndpoints
{
    public static IEndpointRouteBuilder MapPlanVisibleEndpoints(this IEndpointRouteBuilder app)
    {
        var stay = app.MapGroup("/api/v1/stays/me").WithTags("Stay owner").RequireAuthorization("CanListStays");
        stay.MapGet("", StayHandlers.GetMine).ExpiresPlanOnRead();
        stay.MapPut("/plan-visible", PlanVisibleHandlers.SaveStay);

        var taxi = app.MapGroup("/api/v1/taxis/me").WithTags("Taxi driver").RequireAuthorization("CanListTaxis");
        taxi.MapGet("", TaxiHandlers.GetMine).ExpiresPlanOnRead();
        taxi.MapPut("/plan-visible", PlanVisibleHandlers.SaveTaxi);
        return app;
    }
}
