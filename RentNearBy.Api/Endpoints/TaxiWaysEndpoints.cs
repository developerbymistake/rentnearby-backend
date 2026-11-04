using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

// Mapped from TaxiEndpoints, so every route here sits under /api/v1/taxis and the TAXI tab gate.
public static class TaxiWaysEndpoints
{
    public static RouteGroupBuilder MapTaxiWaysEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/ways", TaxiWaysHandlers.GetWays).RequireAuthorization();
        group.MapGet("/ways/plans/{planKey}", TaxiWaysHandlers.GetPlan).RequireAuthorization();
        group.MapGet("/ways/legs/{legKey}/drivers", TaxiWaysHandlers.GetLegDrivers).RequireAuthorization();
        group.MapPost("/ways/ask", TaxiWaysAskHandlers.AskDrivers).RequireAuthorization()
            .AddEndpointFilter<TripExecutionGateFilter>();

        group.MapPost("/journeys", JourneyHandlers.Save).RequireAuthorization();
        group.MapGet("/journeys", JourneyHandlers.List).RequireAuthorization();
        group.MapGet("/journeys/{id:guid}", JourneyHandlers.Get).RequireAuthorization();
        group.MapDelete("/journeys/{id:guid}", JourneyHandlers.Delete).RequireAuthorization();
        group.MapPost("/journeys/{id:guid}/refresh", JourneyHandlers.Refresh).RequireAuthorization();
        group.MapPut("/journeys/{id:guid}/legs/{legIndex:int}/driver", JourneyHandlers.ReplaceDriver).RequireAuthorization();
        group.MapPut("/journeys/{id:guid}/status", JourneyHandlers.SetStatus).RequireAuthorization();

        return group;
    }
}
