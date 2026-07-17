using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class RoomListingsEndpoints
{
    public static RouteGroupBuilder MapListingsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/context", RoomListingsHandlers.GetContext);
        group.MapGet("/nearby", RoomListingsHandlers.GetNearby);
        group.MapGet("/plans", RoomListingsHandlers.GetPlans);
        group.MapGet("/locations/districts", AdminHandlers.GetDistricts);
        group.MapGet("/locations/cities", AdminHandlers.GetCities);
        group.MapGet("/{id:guid}", RoomListingsHandlers.GetById);

        group.MapGet("/my", RoomListingsHandlers.GetMyListings).RequireAuthorization();
        group.MapPost("/", RoomListingsHandlers.CreateListing).RequireAuthorization().DisableAntiforgery();
        group.MapPut("/{id:guid}", RoomListingsHandlers.UpdateListing).RequireAuthorization();
        group.MapDelete("/{id:guid}", RoomListingsHandlers.DeleteListing).RequireAuthorization();

        group.MapPost("/{id:guid}/photos", RoomListingsHandlers.UploadPhoto).RequireAuthorization().DisableAntiforgery();
        group.MapDelete("/{id:guid}/photos/{photoId:guid}", RoomListingsHandlers.DeletePhoto).RequireAuthorization();

        group.MapPost("/{id:guid}/report", RoomListingsHandlers.ReportListing).RequireAuthorization();
        group.MapGet("/{id:guid}/reports", RoomListingsHandlers.GetListingReports).RequireAuthorization();

        // Coin-based Go Live — replaces the old Razorpay-per-listing payment routes entirely.
        group.MapPost("/{listingId:guid}/go-live", GoLiveHandlers.GoLiveRoom).RequireAuthorization();

        return group;
    }
}
