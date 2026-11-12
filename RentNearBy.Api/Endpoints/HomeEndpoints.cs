using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class HomeEndpoints
{
    public static RouteGroupBuilder MapHomeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/feed", HomeHandlers.GetFeed);
        group.MapGet("/nearby-feed", HomeNearbyHandlers.GetNearbyFeed);

        return group;
    }
}
