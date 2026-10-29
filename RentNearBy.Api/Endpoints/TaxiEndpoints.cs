using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class TaxiEndpoints
{
    public static RouteGroupBuilder MapTaxiEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/vehicle-types", AdminTaxiHandlers.GetVehicleTypes);
        group.MapGet("/browse", TaxiHandlers.GetBrowse);
        group.MapGet("/search", TaxiHandlers.GetSearch);
        group.MapGet("/by-slug/{slug}", TaxiHandlers.GetBySlug);

        group.MapGet("/me", TaxiHandlers.GetMine).RequireAuthorization("CanListTaxis");
        group.MapPut("/me/basic", TaxiHandlers.UpsertBasic).RequireAuthorization("CanListTaxis");
        group.MapPost("/me/plate-photo", TaxiHandlers.UploadPlatePhoto).RequireAuthorization("CanListTaxis").DisableAntiforgery();
        group.MapPut("/me/routes", TaxiHandlers.UpsertRoutes).RequireAuthorization("CanListTaxis");
        group.MapPut("/me/routes/{routeId:guid}/fare", TaxiHandlers.UpdateFare).RequireAuthorization("CanListTaxis");
        group.MapPost("/me/submit", TaxiHandlers.Submit).RequireAuthorization("CanListTaxis");
        group.MapPut("/me/availability", TaxiHandlers.UpdateAvailability).RequireAuthorization("CanListTaxis");
        group.MapDelete("/me", TaxiHandlers.DeleteMine).RequireAuthorization("CanListTaxis");

        group.MapGet("/{id:guid}", TaxiHandlers.GetById);
        group.MapPost("/{id:guid}/report", TaxiHandlers.ReportTaxi).RequireAuthorization();
        group.MapGet("/{id:guid}/reports", TaxiHandlers.GetTaxiReports).RequireAuthorization("CanListTaxis");

        return group;
    }
}
