using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class ListingsEndpoints
{
    public static RouteGroupBuilder MapListingsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/context", ListingsHandlers.GetContext);
        group.MapGet("/nearby", ListingsHandlers.GetNearby);
        group.MapGet("/search", ListingsHandlers.Search);
        group.MapGet("/{id:guid}", ListingsHandlers.GetById);

        group.MapGet("/my", ListingsHandlers.GetMyListings).RequireAuthorization();
        group.MapPost("/", ListingsHandlers.CreateListing).RequireAuthorization().DisableAntiforgery();
        group.MapPut("/{id:guid}", ListingsHandlers.UpdateListing).RequireAuthorization();
        group.MapDelete("/{id:guid}", ListingsHandlers.DeleteListing).RequireAuthorization();

        group.MapPost("/{id:guid}/photos", ListingsHandlers.UploadPhoto).RequireAuthorization().DisableAntiforgery();
        group.MapDelete("/{id:guid}/photos/{photoId:guid}", ListingsHandlers.DeletePhoto).RequireAuthorization();

        // Payment endpoints
        group.MapPost("/{listingId:guid}/go-live", PaymentHandlers.InitiatePayment).RequireAuthorization();
        group.MapPost("/{listingId:guid}/verify-payment", PaymentHandlers.VerifyPayment).RequireAuthorization();
        group.MapGet("/payment/status", PaymentHandlers.GetMembershipStatus).RequireAuthorization();

        return group;
    }
}
