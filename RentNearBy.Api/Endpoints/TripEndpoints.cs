using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class TripEndpoints
{
    public static RouteGroupBuilder MapTripEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/cities", TripHandlers.GetCities).RequireAuthorization();
        group.MapGet("/cities/nearest", TripHandlers.GetNearestCity).RequireAuthorization();
        group.MapGet("/cities/version", TripHandlers.GetCitiesVersion).RequireAuthorization();
        group.MapGet("/cities/{cityKey:long}/spots", TripHandlers.GetCitySpots).RequireAuthorization();
        group.MapGet("/cities/{cityKey:long}/spots/{spotId}", TripHandlers.GetCitySpot).RequireAuthorization();
        group.MapPost("/cities/{cityKey:long}/spots/minutes", TripHandlers.GetSpotMinutes).RequireAuthorization();
        group.MapGet("/cities/{cityKey:long}/supply", TripHandlers.GetCitySupply).RequireAuthorization();
        group.MapPost("/plan", TripHandlers.Plan).RequireAuthorization();

        group.MapGet("/", TripHandlers.GetMine).RequireAuthorization();
        group.MapPost("/", TripHandlers.Create).RequireAuthorization();
        group.MapGet("/{id:guid}", TripHandlers.GetById).RequireAuthorization();
        group.MapGet("/{id:guid}/invite", TripHandlers.GetInvite).RequireAuthorization();
        group.MapPut("/{id:guid}", TripHandlers.Update).RequireAuthorization();
        group.MapPost("/{id:guid}/complete-plan", TripHandlers.CompletePlan).RequireAuthorization();
        group.MapDelete("/{id:guid}", TripHandlers.Delete).RequireAuthorization();

        group.MapTripExecutionEndpoints();

        return group;
    }
}
