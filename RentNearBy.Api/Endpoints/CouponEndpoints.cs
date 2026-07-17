using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class CouponEndpoints
{
    public static RouteGroupBuilder MapCouponEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/redeem", CouponHandlers.RedeemCoupon).RequireAuthorization();
        return group;
    }
}
