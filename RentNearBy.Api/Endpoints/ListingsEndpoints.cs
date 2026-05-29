using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class RoomListingsEndpoints
{
    public static RouteGroupBuilder MapListingsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/context", RoomListingsHandlers.GetContext);
        group.MapGet("/nearby", RoomListingsHandlers.GetNearby);
        group.MapGet("/search", RoomListingsHandlers.Search);
        group.MapGet("/plans", RoomListingsHandlers.GetPlans);
        group.MapGet("/{id:guid}", RoomListingsHandlers.GetById);

        group.MapGet("/my", RoomListingsHandlers.GetMyListings).RequireAuthorization();
        group.MapPost("/", RoomListingsHandlers.CreateListing).RequireAuthorization().DisableAntiforgery();
        group.MapPut("/{id:guid}", RoomListingsHandlers.UpdateListing).RequireAuthorization();
        group.MapDelete("/{id:guid}", RoomListingsHandlers.DeleteListing).RequireAuthorization();

        group.MapPost("/{id:guid}/photos", RoomListingsHandlers.UploadPhoto).RequireAuthorization().DisableAntiforgery();
        group.MapDelete("/{id:guid}/photos/{photoId:guid}", RoomListingsHandlers.DeletePhoto).RequireAuthorization();

        // Payment endpoints
        group.MapPost("/{listingId:guid}/create-order", PaymentHandlers.CreateOrder).RequireAuthorization();
        group.MapPost("/{listingId:guid}/go-live", PaymentHandlers.InitiatePayment).RequireAuthorization();
        group.MapPost("/{listingId:guid}/verify-payment", PaymentHandlers.VerifyPayment).RequireAuthorization();
        group.MapGet("/payment/status", PaymentHandlers.GetMembershipStatus).RequireAuthorization();
        group.MapPost("/upgrade-plan/create-order", PaymentHandlers.CreateUpgradeOrder).RequireAuthorization();
        group.MapPost("/upgrade-plan/verify", PaymentHandlers.VerifyUpgradePayment).RequireAuthorization();

        return group;
    }
}
