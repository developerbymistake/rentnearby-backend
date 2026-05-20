using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/districts", AdminHandlers.GetDistricts);
        group.MapPost("/districts", AdminHandlers.CreateDistrict).RequireAuthorization("AdminOnly");
        group.MapDelete("/districts/{id:guid}", AdminHandlers.DeleteDistrict).RequireAuthorization("AdminOnly");

        group.MapGet("/cities", AdminHandlers.GetCities);
        group.MapPost("/cities", AdminHandlers.CreateCity).RequireAuthorization("AdminOnly");
        group.MapDelete("/cities/{id:guid}", AdminHandlers.DeleteCity).RequireAuthorization("AdminOnly");

        group.MapGet("/room-types", AdminHandlers.GetRoomTypes);
        group.MapPost("/room-types", AdminHandlers.CreateRoomType).RequireAuthorization("AdminOnly");
        group.MapPut("/room-types/{id:guid}", AdminHandlers.UpdateRoomType).RequireAuthorization("AdminOnly");
        group.MapDelete("/room-types/{id:guid}", AdminHandlers.DeleteRoomType).RequireAuthorization("AdminOnly");

        group.MapGet("/payment-feature", AdminHandlers.GetPaymentFeature);
        group.MapPut("/payment-feature", AdminHandlers.UpdatePaymentFeature).RequireAuthorization("AdminOnly");

        group.MapGet("/plot-payment-feature", AdminHandlers.GetPlotPaymentFeature);
        group.MapPut("/plot-payment-feature", AdminHandlers.UpdatePlotPaymentFeature).RequireAuthorization("AdminOnly");

        group.MapGet("/stats", AdminHandlers.GetStats).RequireAuthorization("AdminOnly");

        group.MapGet("/geocode", AdminHandlers.Geocode).RequireAuthorization("AdminOnly");

        group.MapGet("/users", AdminHandlers.GetUsers).RequireAuthorization("AdminOnly");
        group.MapPut("/users/{id:guid}/status", AdminHandlers.UpdateUserStatus).RequireAuthorization("AdminOnly");
        group.MapPost("/users/{id:guid}/activate-membership", AdminHandlers.ActivateMembership).RequireAuthorization("AdminOnly");

        group.MapGet("/transactions", AdminHandlers.GetTransactions).RequireAuthorization("AdminOnly");

        group.MapGet("/plans", AdminHandlers.GetPlans).RequireAuthorization("AdminOnly");
        group.MapPost("/plans", AdminHandlers.CreatePlan).RequireAuthorization("AdminOnly");
        group.MapPut("/plans/{id:guid}", AdminHandlers.UpdatePlan).RequireAuthorization("AdminOnly");

        group.MapGet("/plot-plans", AdminHandlers.GetPlotPlans).RequireAuthorization("AdminOnly");
        group.MapPost("/plot-plans", AdminHandlers.CreatePlotPlan).RequireAuthorization("AdminOnly");
        group.MapPut("/plot-plans/{id:guid}", AdminHandlers.UpdatePlotPlan).RequireAuthorization("AdminOnly");
        group.MapPost("/users/{id:guid}/activate-plot-membership", AdminHandlers.ActivatePlotMembership).RequireAuthorization("AdminOnly");

        group.MapGet("/listings", AdminHandlers.GetAdminListings).RequireAuthorization("AdminOnly");
        group.MapPut("/listings/{id:guid}/status", AdminHandlers.ToggleAdminListingStatus).RequireAuthorization("AdminOnly");
        group.MapDelete("/listings/{id:guid}", AdminHandlers.DeleteAdminListing).RequireAuthorization("AdminOnly");

        return group;
    }
}
