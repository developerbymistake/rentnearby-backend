using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

// Never tab-gated: the district gate and every setup map need these whatever tabs are on.
public static class LocationsEndpoints
{
    public static RouteGroupBuilder MapLocationsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/context", RoomListingsHandlers.GetContext);
        group.MapGet("/districts", AdminHandlers.GetDistricts);
        group.MapGet("/districts/{id:guid}/boundary", AdminHandlers.GetDistrictBoundary);
        group.MapGet("/cities", AdminHandlers.GetCities);

        return group;
    }
}
