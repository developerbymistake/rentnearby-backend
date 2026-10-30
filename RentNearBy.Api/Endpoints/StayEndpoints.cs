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
        group.MapGet("/by-slug/{slug}", StayHandlers.GetBySlug);

        group.MapGet("/me", StayHandlers.GetMine).RequireAuthorization("CanListStays");
        group.MapPut("/me/basic", StayHandlers.UpsertBasic).RequireAuthorization("CanListStays");
        group.MapPost("/me/photos", StayHandlers.UploadPhoto).RequireAuthorization("CanListStays").DisableAntiforgery();
        group.MapDelete("/me/photos/{photoId:guid}", StayHandlers.DeletePhoto).RequireAuthorization("CanListStays");
        group.MapPut("/me/rooms", StayHandlers.UpsertRooms).RequireAuthorization("CanListStays");
        group.MapPut("/me/rooms/{roomId:guid}/price", StayHandlers.UpdateRoomPrice).RequireAuthorization("CanListStays");
        group.MapPut("/me/rules", StayHandlers.UpsertRules).RequireAuthorization("CanListStays");
        group.MapPost("/me/submit", StayHandlers.Submit).RequireAuthorization("CanListStays");
        group.MapPut("/me/accepting", StayHandlers.UpdateAccepting).RequireAuthorization("CanListStays");
        group.MapDelete("/me", StayHandlers.DeleteMine).RequireAuthorization("CanListStays");

        group.MapGet("/{id:guid}", StayHandlers.GetById);
        group.MapPost("/{id:guid}/report", StayHandlers.ReportStay).RequireAuthorization();
        group.MapGet("/{id:guid}/reports", StayHandlers.GetStayReports).RequireAuthorization("CanListStays");

        return group;
    }
}
