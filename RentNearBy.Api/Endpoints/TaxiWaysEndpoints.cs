using RentNearBy.Api.Extensions;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

// Mapped from TaxiEndpoints, so every route here sits under /api/v1/taxis and the TAXI tab gate.
public static class TaxiWaysEndpoints
{
    public static RouteGroupBuilder MapTaxiWaysEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/ways", TaxiWaysHandlers.GetWays).RequireAuthorization();
        group.MapGet("/ways/available", TaxiWaysHandlers.GetWaysAvailable).RequireAuthorization();
        group.MapGet("/ways/plans/{planKey}", TaxiWaysHandlers.GetPlan).RequireAuthorization();
        group.MapGet("/ways/legs/{legKey}/drivers", TaxiWaysHandlers.GetLegDrivers).RequireAuthorization();
        group.MapPost("/ways/ask", TaxiWaysAskHandlers.AskDrivers).SeekerOnly()
            .AddEndpointFilter<TripExecutionGateFilter>();

        group.MapPost("/journeys", JourneyHandlers.Save).SeekerOnly();
        group.MapGet("/journeys", JourneyHandlers.List).SeekerOnly();
        group.MapGet("/journeys/{id:guid}", JourneyHandlers.Get).SeekerOnly();
        group.MapDelete("/journeys/{id:guid}", JourneyHandlers.Delete).SeekerOnly();
        group.MapPost("/journeys/{id:guid}/refresh", JourneyHandlers.Refresh).SeekerOnly();
        group.MapPut("/journeys/{id:guid}/legs/{legIndex:int}/driver", JourneyHandlers.ReplaceDriver).SeekerOnly();
        group.MapPut("/journeys/{id:guid}/status", JourneyHandlers.SetStatus).SeekerOnly();

        return group;
    }
}
