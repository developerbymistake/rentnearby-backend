using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class CreditPackEndpoints
{
    public static RouteGroupBuilder MapCreditPackEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", CreditPackHandlers.GetActivePacks).AllowAnonymous();
        group.MapPost("/create-order", CreditPackHandlers.CreateOrder).RequireAuthorization();
        group.MapPost("/verify-payment", CreditPackHandlers.VerifyPayment).RequireAuthorization();
        group.MapPost("/cancel-order", CreditPackHandlers.CancelOrder).RequireAuthorization();
        group.MapGet("/purchases/latest", CreditPackHandlers.GetLatestPurchase).RequireAuthorization();
        return group;
    }
}
