using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class TripEndpoints
{
    public static RouteGroupBuilder MapTripEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/cities", TripHandlers.GetCities).RequireAuthorization();
        group.MapGet("/cities/nearest", TripHandlers.GetNearestCity).RequireAuthorization();
        group.MapGet("/cities/version", TripHandlers.GetCitiesVersion).RequireAuthorization();
        group.MapGet("/cities/{cityKey:long}/spots", TripHandlers.GetCitySpots).SeekerOnly();
        group.MapGet("/cities/{cityKey:long}/spots/{spotId}", TripHandlers.GetCitySpot).RequireAuthorization();
        group.MapPost("/cities/{cityKey:long}/spots/minutes", TripHandlers.GetSpotMinutes).SeekerOnly();
        group.MapGet("/cities/{cityKey:long}/supply", TripHandlers.GetCitySupply).SeekerOnly();
        group.MapGet("/map-packs/{stateId:guid}/{version}", TripMapPackHandlers.GetMapPack).RequireAuthorization();
        group.MapPost("/plan", TripHandlers.Plan).SeekerOnly();

        group.MapGet("/", TripHandlers.GetMine).SeekerOnly();
        group.MapPost("/", TripHandlers.Create).SeekerOnly();
        group.MapGet("/{id:guid}", TripHandlers.GetById).SeekerOnly();
        group.MapGet("/{id:guid}/invite", TripHandlers.GetInvite).SeekerOnly();
        group.MapPut("/{id:guid}", TripHandlers.Update).SeekerOnly();
        group.MapPost("/{id:guid}/complete-plan", TripHandlers.CompletePlan).SeekerOnly();
        group.MapDelete("/{id:guid}", TripHandlers.Delete).SeekerOnly();

        group.MapTripExecutionEndpoints();

        return group;
    }
}
