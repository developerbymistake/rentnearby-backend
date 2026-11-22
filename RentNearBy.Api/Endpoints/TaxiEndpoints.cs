using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class TaxiEndpoints
{
    public static RouteGroupBuilder MapTaxiEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/vehicle-types", AdminTaxiHandlers.GetVehicleTypes);
        group.MapGet("/browse", TaxiHandlers.GetBrowse);
        group.MapGet("/search", TaxiHandlers.GetSearch);
        group.MapGet("/feed", TaxiFeedHandlers.GetFeed);
        group.MapGet("/route-feed", TaxiFeedHandlers.GetRouteFeed).RequireAuthorization();
        group.MapGet("/by-slug/{slug}", TaxiHandlers.GetBySlug);
        group.MapTaxiWaysEndpoints();

        // GET /me is mapped ungated by lane BE3 (Endpoints/Owner/PlanVisibleEndpoints.cs, contract R-5).
        group.MapPut("/me/basic", TaxiHandlers.UpsertBasic).RequireAuthorization("CanListTaxis").OwnerWriteLimited();
        group.MapPost("/me/plate-photo", TaxiHandlers.UploadPlatePhoto).RequireAuthorization("CanListTaxis").DisableAntiforgery().OwnerPhotoLimited().PhotoBodyLimited();
        group.MapPost("/me/display-photo", TaxiHandlers.UploadDisplayPhoto).RequireAuthorization("CanListTaxis").DisableAntiforgery().OwnerPhotoLimited().PhotoBodyLimited();
        group.MapPut("/me/routes", TaxiHandlers.UpsertRoutes).RequireAuthorization("CanListTaxis").OwnerWriteLimited();
        group.MapPut("/me/routes/{routeId:guid}/fare", TaxiHandlers.UpdateFare).RequireAuthorization("CanListTaxis").OwnerWriteLimited();
        group.MapPut("/me/local-rate", TaxiHandlers.UpdateLocalRate).RequireAuthorization("CanListTaxis").OwnerWriteLimited();
        group.MapPost("/me/submit", TaxiHandlers.Submit).RequireAuthorization("CanListTaxis").OwnerWriteLimited();
        group.MapPut("/me/availability", TaxiHandlers.UpdateAvailability).RequireAuthorization("CanListTaxis").OwnerWriteLimited();
        group.MapDelete("/me", TaxiHandlers.DeleteMine).RequireAuthorization("CanListTaxis").OwnerWriteLimited();

        group.MapGet("/{id:guid}", TaxiHandlers.GetById);
        group.MapPost("/{id:guid}/report", TaxiHandlers.ReportTaxi).RequireAuthorization();
        group.MapGet("/{id:guid}/reports", TaxiHandlers.GetTaxiReports).RequireAuthorization("CanListTaxis");

        return group;
    }
}
