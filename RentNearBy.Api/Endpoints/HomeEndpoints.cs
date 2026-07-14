using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class HomeEndpoints
{
    public static RouteGroupBuilder MapHomeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/summary", HomeHandlers.GetSummary);
        group.MapGet("/rooms", HomeHandlers.GetRooms);
        group.MapGet("/plots", HomeHandlers.GetPlots);

        return group;
    }
}
