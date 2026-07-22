using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class CoinPackEndpoints
{
    public static RouteGroupBuilder MapCoinPackEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", CoinPackHandlers.GetActivePacks).AllowAnonymous();
        group.MapPost("/create-order", CoinPackHandlers.CreateOrder).RequireAuthorization();
        group.MapPost("/verify-payment", CoinPackHandlers.VerifyPayment).RequireAuthorization();
        group.MapPost("/cancel-order", CoinPackHandlers.CancelOrder).RequireAuthorization();
        group.MapGet("/purchases/latest", CoinPackHandlers.GetLatestPurchase).RequireAuthorization();
        return group;
    }
}
