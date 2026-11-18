using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers.TaxiRequests;

namespace RentNearBy.Api.Endpoints;

// Driver routes (contract section 6, D1 to D12, policy CanListTaxis) and the ring ack (section 7, anonymous), mapped on the
// /api/v1/taxi-requests group. Gated = TripExecutionGateFilter; open routes keep working while the switch is off.
public static class TaxiDriverRequestEndpoints
{
    public const string Policy = "CanListTaxis";

    public static RouteGroupBuilder MapTaxiDriverRequestEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/driver/offers", TaxiDriverRequestHandlers.ListOffers).RequireAuthorization(Policy);
        group.MapGet("/driver/offers/{offerId:guid}", TaxiDriverRequestHandlers.GetOffer).RequireAuthorization(Policy);
        group.MapPost("/driver/offers/{offerId:guid}/accept", TaxiDriverRequestHandlers.Accept).Gated();
        group.MapPost("/driver/offers/{offerId:guid}/decline", TaxiDriverRequestHandlers.Decline).RequireAuthorization(Policy);
        group.MapPost("/driver/offers/{offerId:guid}/confirm", TaxiDriverRequestHandlers.Confirm).Gated();
        group.MapPost("/driver/offers/{offerId:guid}/withdraw", TaxiDriverRequestHandlers.Withdraw).RequireAuthorization(Policy);
        group.MapPost("/driver/bookings/{bookingId:guid}/end", TaxiDriverRequestHandlers.EndBooking).RequireAuthorization(Policy);
        group.MapGet("/driver/bookings", TaxiDriverRequestHandlers.ListBookings).RequireAuthorization(Policy);
        group.MapGet("/driver/rating", TaxiDriverRequestHandlers.Rating).RequireAuthorization(Policy);
        group.MapGet("/driver/readiness", TaxiDriverRequestHandlers.GetReadiness).RequireAuthorization(Policy);
        group.MapPut("/driver/readiness", TaxiDriverRequestHandlers.PutReadiness).RequireAuthorization(Policy);
        group.MapPost("/driver/test-ring", TaxiDriverRequestHandlers.TestRing).RequireAuthorization(Policy);

        group.MapPost("/offers/{offerId:guid}/ack", TaxiAckHandlers.Ack).AllowAnonymous();
        return group;
    }

    private static RouteHandlerBuilder Gated(this RouteHandlerBuilder route)
        => route.RequireAuthorization(Policy).AddEndpointFilter<TripExecutionGateFilter>();
}
