using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class HomeEndpoints
{
    public static RouteGroupBuilder MapHomeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/feed", HomeHandlers.GetFeed);
        group.MapGet("/nearby-feed", HomeNearbyHandlers.GetNearbyFeed);
        group.MapGet("/seeker-feed", RentNearBy.Api.Handlers.Owner.Be4.SeekerFeedHandlers.Get).RequireAuthorization();

        return group;
    }
}
