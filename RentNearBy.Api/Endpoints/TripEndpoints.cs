using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class TripEndpoints
{
    public static RouteGroupBuilder MapTripEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/cities/{cityId:guid}/spots", TripHandlers.GetCitySpots).RequireAuthorization();
        group.MapPost("/plan", TripHandlers.Plan).RequireAuthorization();

        group.MapGet("/", TripHandlers.GetMine).RequireAuthorization();
        group.MapPost("/", TripHandlers.Create).RequireAuthorization();
        group.MapGet("/{id:guid}", TripHandlers.GetById).RequireAuthorization();
        group.MapPut("/{id:guid}", TripHandlers.Update).RequireAuthorization();
        group.MapDelete("/{id:guid}", TripHandlers.Delete).RequireAuthorization();

        return group;
    }
}
