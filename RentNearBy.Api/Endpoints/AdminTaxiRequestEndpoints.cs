using RentNearBy.Api.Handlers.TaxiRequests;

namespace RentNearBy.Api.Endpoints;

// Mapped on the existing admin group (/api/v1/admin) by the wiring package; same AdminOnly policy as AdminEndpoints.
public static class AdminTaxiRequestEndpoints
{
    public static RouteGroupBuilder MapAdminTaxiRequestEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/taxi-requests", AdminTaxiRequestHandlers.GetRequests).RequireAuthorization("AdminOnly");
        group.MapGet("/taxi-requests/health", AdminTaxiRequestHandlers.GetHealth).RequireAuthorization("AdminOnly");
        group.MapGet("/taxi-requests/{id:guid}", AdminTaxiRequestHandlers.GetRequest).RequireAuthorization("AdminOnly");
        group.MapPost("/taxi-requests/{id:guid}/cancel", AdminTaxiRequestHandlers.CancelRequest).RequireAuthorization("AdminOnly");
        group.MapGet("/taxi-bookings", AdminTaxiRequestHandlers.GetBookings).RequireAuthorization("AdminOnly");
        return group;
    }
}
