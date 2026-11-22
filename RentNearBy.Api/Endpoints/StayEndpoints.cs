using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class StayEndpoints
{
    public static RouteGroupBuilder MapStayEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/types", AdminStayHandlers.GetStayTypes);
        group.MapGet("/registration-types", AdminStayHandlers.GetStayRegistrationTypes);
        group.MapGet("/browse", StayHandlers.GetBrowse);
        group.MapGet("/nearby", StayHandlers.GetNearby);
        group.MapGet("/nearest", StayHandlers.GetNearest);
        group.MapGet("/feed", StayFeedHandlers.GetFeed);
        group.MapGet("/by-slug/{slug}", StayHandlers.GetBySlug);

        // GET /me is mapped ungated by lane BE3 (Endpoints/Owner/PlanVisibleEndpoints.cs, contract R-5).
        group.MapPut("/me/basic", StayHandlers.UpsertBasic).RequireAuthorization("CanListStays").OwnerWriteLimited();
        group.MapPost("/me/photos", StayHandlers.UploadPhoto).RequireAuthorization("CanListStays").DisableAntiforgery().OwnerPhotoLimited().PhotoBodyLimited();
        group.MapDelete("/me/photos/{photoId:guid}", StayHandlers.DeletePhoto).RequireAuthorization("CanListStays").OwnerWriteLimited();
        group.MapPut("/me/photos/{photoId:guid}", StayHandlers.ReplacePhoto).RequireAuthorization("CanListStays").DisableAntiforgery().OwnerPhotoLimited().PhotoBodyLimited();
        group.MapPut("/me/rooms", StayHandlers.UpsertRooms).RequireAuthorization("CanListStays").OwnerWriteLimited();
        group.MapPut("/me/rooms/{roomId:guid}/price", StayHandlers.UpdateRoomPrice).RequireAuthorization("CanListStays").OwnerWriteLimited();
        group.MapPut("/me/rules", StayHandlers.UpsertRules).RequireAuthorization("CanListStays").OwnerWriteLimited();
        group.MapPost("/me/submit", StayHandlers.Submit).RequireAuthorization("CanListStays").OwnerWriteLimited();
        group.MapPut("/me/accepting", StayHandlers.UpdateAccepting).RequireAuthorization("CanListStays").OwnerWriteLimited();
        group.MapPut("/me/share-details", StayHandlers.UpdateShareDetails).RequireAuthorization("CanListStays").OwnerWriteLimited();
        group.MapDelete("/me", StayHandlers.DeleteMine).RequireAuthorization("CanListStays").OwnerWriteLimited();

        group.MapGet("/{id:guid}", StayHandlers.GetById);
        group.MapPost("/{id:guid}/report", StayHandlers.ReportStay).RequireAuthorization();
        group.MapGet("/{id:guid}/reports", StayHandlers.GetStayReports).RequireAuthorization("CanListStays");

        return group;
    }
}
