using RentNearBy.Api.Handlers.Buddy.Be4;

namespace RentNearBy.Api.Endpoints;

// E9, E10, E12, E18, E19 under /api/v1/trips (the group carries TripPlannerGateFilter). No UPI route.
public static class TripPaymentEndpoints
{
    public static RouteGroupBuilder MapTripPaymentEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{tripId:guid}/payments/said-paid", TripPaymentHandlers.SaidPaid).RequireAuthorization();
        group.MapPost("/{tripId:guid}/payments/remind", TripPaymentHandlers.Remind).RequireAuthorization();
        group.MapPost("/{tripId:guid}/payments/{paymentId:guid}/received", TripPaymentHandlers.Received).RequireAuthorization();
        group.MapPost("/{tripId:guid}/payments/{paymentId:guid}/withdraw", TripPaymentHandlers.Withdraw).RequireAuthorization();
        group.MapPost("/{tripId:guid}/payments/{paymentId:guid}/not-received", TripPaymentHandlers.NotReceived).RequireAuthorization();
        return group;
    }
}
