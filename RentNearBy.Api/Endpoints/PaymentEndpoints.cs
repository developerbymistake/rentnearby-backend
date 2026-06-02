using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class PaymentEndpoints
{
    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/cancel-order", PaymentHandlers.CancelOrder).RequireAuthorization();
        return group;
    }
}
