using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminTaxiEndpoints
{
    public static RouteGroupBuilder MapAdminTaxiEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/vehicle-types", AdminTaxiHandlers.GetAllVehicleTypes).RequireAuthorization("AdminOnly");
        group.MapPost("/vehicle-types", AdminTaxiHandlers.CreateVehicleType).RequireAuthorization("AdminOnly");
        group.MapPut("/vehicle-types/{id:guid}", AdminTaxiHandlers.UpdateVehicleType).RequireAuthorization("AdminOnly");
        group.MapDelete("/vehicle-types/{id:guid}", AdminTaxiHandlers.DeleteVehicleType).RequireAuthorization("AdminOnly");

        // Popular routes (contract 1.6): only the admin decides them.
        group.MapGet("/popular-routes", AdminPopularRouteHandlers.List).RequireAuthorization("AdminOnly");
        group.MapPost("/popular-routes", AdminPopularRouteHandlers.Create).RequireAuthorization("AdminOnly");
        group.MapPut("/popular-routes/order", AdminPopularRouteHandlers.Reorder).RequireAuthorization("AdminOnly");
        group.MapDelete("/popular-routes/{id:guid}", AdminPopularRouteHandlers.Delete).RequireAuthorization("AdminOnly");
        group.MapPut("/popular-routes/{id:guid}/active", AdminPopularRouteHandlers.SetActive).RequireAuthorization("AdminOnly");

        group.MapGet("/", AdminTaxiHandlers.GetTaxis).RequireAuthorization("AdminOnly");
        group.MapGet("/{id:guid}", AdminTaxiHandlers.GetTaxiById).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/approve", AdminTaxiHandlers.ApproveTaxi).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/reject", AdminTaxiHandlers.RejectTaxi).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/unpublish", AdminTaxiHandlers.UnpublishTaxi).RequireAuthorization("AdminOnly");
        group.MapDelete("/{id:guid}", AdminTaxiHandlers.DeleteTaxi).RequireAuthorization("AdminOnly");
        group.MapDelete("/{id:guid}/display-photo", AdminTaxiHandlers.RemoveDisplayPhoto).RequireAuthorization("AdminOnly");

        return group;
    }
}
