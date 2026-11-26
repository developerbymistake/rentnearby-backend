using RentNearBy.Api.Extensions;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers.TaxiRequests;

namespace RentNearBy.Api.Endpoints;

// Traveller routes of the taxi request system (contract section 5), all SeekerOnly. Gated = TripExecutionGateFilter (master switch off gives
// 403 TripExecutionDisabled); open routes keep working so a paused system can still be wound down.
public static class TaxiRequestEndpoints
{
    // T1 to T3, mapped on the /api/v1/trips group.
    public static RouteGroupBuilder MapTripTaxiRequestEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{tripId:guid}/taxi-request/pickup-places", TaxiRequestHandlers.PickupPlaces).SeekerOnly();
        group.MapPost("/{tripId:guid}/taxi-request", TaxiRequestHandlers.Create).Gated();
        group.MapGet("/{tripId:guid}/taxi-request", TaxiRequestHandlers.GetForTrip).SeekerOnly();
        return group;
    }

    // T4 to T10, mapped on the /api/v1/taxi-requests group.
    public static RouteGroupBuilder MapTaxiRequestEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{requestId:guid}", TaxiRequestHandlers.Get).SeekerOnly();
        group.MapPost("/{requestId:guid}/cancel", TaxiRequestHandlers.Cancel).SeekerOnly();
        group.MapPost("/{requestId:guid}/ask-more", TaxiRequestHandlers.AskMore).Gated();
        group.MapPost("/{requestId:guid}/offers/{offerId:guid}/select", TaxiRequestHandlers.Select).Gated();
        group.MapPost("/{requestId:guid}/offers/{offerId:guid}/not-agreed", TaxiRequestHandlers.NotAgreed).SeekerOnly();
        group.MapPost("/{requestId:guid}/end", TaxiRequestHandlers.End).SeekerOnly();
        group.MapPost("/{requestId:guid}/rating", TaxiRequestHandlers.Rate).SeekerOnly();
        return group;
    }

    private static RouteHandlerBuilder Gated(this RouteHandlerBuilder route)
        => route.SeekerOnly().AddEndpointFilter<TripExecutionGateFilter>();
}
