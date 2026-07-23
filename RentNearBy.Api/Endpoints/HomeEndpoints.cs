using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class HomeEndpoints
{
    public static RouteGroupBuilder MapHomeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/rooms", HomeHandlers.GetRooms);
        group.MapGet("/plots", HomeHandlers.GetPlots);
        group.MapGet("/rooms/browse", HomeHandlers.GetRoomsBrowse);
        group.MapGet("/plots/browse", HomeHandlers.GetPlotsBrowse);
        group.MapGet("/rooms/recent", HomeHandlers.GetRecentRooms);
        group.MapGet("/plots/recent", HomeHandlers.GetRecentPlots);

        return group;
    }
}
