using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class WalletEndpoints
{
    public static RouteGroupBuilder MapWalletEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/balance", WalletHandlers.GetBalance).RequireAuthorization();
        group.MapGet("/transactions", WalletHandlers.GetMyTransactions).RequireAuthorization();
        return group;
    }
}
