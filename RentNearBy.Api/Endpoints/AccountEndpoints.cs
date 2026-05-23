using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account").WithTags("Account");
        group.MapDelete("/", AccountHandlers.DeleteAccount).RequireAuthorization();
    }
}
