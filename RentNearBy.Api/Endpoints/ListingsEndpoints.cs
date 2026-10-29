using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class RoomListingsEndpoints
{
    public static RouteGroupBuilder MapListingsEndpoints(this RouteGroupBuilder group)
    {
        // App-wide (district gate, every setup map); also served ungated under /api/v1/locations.
        group.MapGet("/context", RoomListingsHandlers.GetContext).WithMetadata(new SkipTabGateAttribute());
        group.MapGet("/nearby", RoomListingsHandlers.GetNearby);
        group.MapGet("/nearest", RoomListingsHandlers.GetNearest);
        group.MapGet("/locations/districts", AdminHandlers.GetDistricts).WithMetadata(new SkipTabGateAttribute());
        group.MapGet("/locations/districts/{id:guid}/boundary", AdminHandlers.GetDistrictBoundary).WithMetadata(new SkipTabGateAttribute());
        group.MapGet("/locations/cities", AdminHandlers.GetCities).WithMetadata(new SkipTabGateAttribute());
        // Public (no-auth) share-link/QR resolver — see RoomListingsHandlers.GetBySlug's doc comment.
        group.MapGet("/by-slug/{slug}", RoomListingsHandlers.GetBySlug);
        group.MapGet("/{id:guid}", RoomListingsHandlers.GetById);

        group.MapGet("/my", RoomListingsHandlers.GetMyListings).RequireAuthorization("CanListRooms");
        group.MapPost("/", RoomListingsHandlers.CreateListing).RequireAuthorization("CanListRooms").DisableAntiforgery();
        group.MapPut("/{id:guid}", RoomListingsHandlers.UpdateListing).RequireAuthorization("CanListRooms");
        group.MapDelete("/{id:guid}", RoomListingsHandlers.DeleteListing).RequireAuthorization("CanListRooms");

        group.MapPost("/{id:guid}/photos", RoomListingsHandlers.UploadPhoto).RequireAuthorization("CanListRooms").DisableAntiforgery();
        group.MapDelete("/{id:guid}/photos/{photoId:guid}", RoomListingsHandlers.DeletePhoto).RequireAuthorization("CanListRooms");

        group.MapPost("/{id:guid}/report", RoomListingsHandlers.ReportListing).RequireAuthorization();
        group.MapGet("/{id:guid}/reports", RoomListingsHandlers.GetListingReports).RequireAuthorization("CanListRooms");

        // Plan-limited Go Live (no body) and live-slot swap.
        group.MapPost("/{listingId:guid}/go-live", GoLiveHandlers.GoLiveRoom).RequireAuthorization("CanListRooms");
        group.MapPost("/{id:guid}/swap-live", RoomListingsHandlers.SwapLive).RequireAuthorization("CanListRooms");

        return group;
    }
}
