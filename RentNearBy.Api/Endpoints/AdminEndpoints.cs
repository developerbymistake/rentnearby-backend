using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/districts", AdminHandlers.GetDistricts);
        group.MapPost("/districts", AdminHandlers.CreateDistrict).RequireAuthorization("AdminOnly");
        group.MapPut("/districts/{id:guid}/active", AdminHandlers.ToggleDistrictActive).RequireAuthorization("AdminOnly");
        group.MapPut("/districts/{id:guid}/force-active", AdminHandlers.ForceActivateDistrict).RequireAuthorization("AdminOnly");
        group.MapDelete("/districts/{id:guid}", AdminHandlers.DeleteDistrict).RequireAuthorization("AdminOnly");

        group.MapGet("/states", AdminHandlers.GetStates);
        group.MapPut("/states/{stateName}/active", AdminHandlers.BulkToggleStateActive).RequireAuthorization("AdminOnly");

        group.MapGet("/cities", AdminHandlers.GetCities);
        group.MapPost("/cities", AdminHandlers.CreateCity).RequireAuthorization("AdminOnly");
        group.MapDelete("/cities/{id:guid}", AdminHandlers.DeleteCity).RequireAuthorization("AdminOnly");

        group.MapGet("/room-types", AdminHandlers.GetRoomTypes);
        group.MapPost("/room-types", AdminHandlers.CreateRoomType).RequireAuthorization("AdminOnly");
        group.MapPut("/room-types/{id:guid}", AdminHandlers.UpdateRoomType).RequireAuthorization("AdminOnly");
        group.MapDelete("/room-types/{id:guid}", AdminHandlers.DeleteRoomType).RequireAuthorization("AdminOnly");

        group.MapGet("/plot-types", AdminHandlers.GetPlotTypes);
        group.MapPost("/plot-types", AdminHandlers.CreatePlotType).RequireAuthorization("AdminOnly");
        group.MapPut("/plot-types/{id:guid}", AdminHandlers.UpdatePlotType).RequireAuthorization("AdminOnly");
        group.MapDelete("/plot-types/{id:guid}", AdminHandlers.DeletePlotType).RequireAuthorization("AdminOnly");

        group.MapGet("/report-reasons", AdminHandlers.GetReportReasons);
        group.MapPost("/report-reasons", AdminHandlers.CreateReportReason).RequireAuthorization("AdminOnly");
        group.MapPut("/report-reasons/{id:guid}", AdminHandlers.UpdateReportReason).RequireAuthorization("AdminOnly");
        group.MapDelete("/report-reasons/{id:guid}", AdminHandlers.DeleteReportReason).RequireAuthorization("AdminOnly");

        group.MapGet("/features", AdminHandlers.GetAllFeatures);
        group.MapGet("/features/{key}", AdminHandlers.GetFeatureByKey);
        group.MapPut("/features/{key}", AdminHandlers.UpdateFeature).RequireAuthorization("AdminOnly");

        group.MapGet("/stats", AdminHandlers.GetStats).RequireAuthorization("AdminOnly");

        group.MapGet("/geocode", AdminHandlers.Geocode).RequireAuthorization("AdminOnly");

        group.MapGet("/users", AdminHandlers.GetUsers).RequireAuthorization("AdminOnly");
        group.MapGet("/users/{id:guid}", AdminHandlers.GetUserById).RequireAuthorization("AdminOnly");
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
        group.MapGet("/listings/{id:guid}", AdminHandlers.GetAdminListingById).RequireAuthorization("AdminOnly");
        group.MapPut("/listings/{id:guid}/status", AdminHandlers.ToggleAdminListingStatus).RequireAuthorization("AdminOnly");
        group.MapDelete("/listings/{id:guid}", AdminHandlers.DeleteAdminListing).RequireAuthorization("AdminOnly");

        group.MapGet("/reports", AdminHandlers.GetReports).RequireAuthorization("AdminOnly");
        group.MapGet("/reports/{id:guid}", AdminHandlers.GetReportById).RequireAuthorization("AdminOnly");
        group.MapPut("/reports/{id:guid}/resolve", AdminHandlers.ResolveReport).RequireAuthorization("AdminOnly");

        group.MapPost("/broadcast-notification", BroadcastHandlers.SendBroadcast).RequireAuthorization("AdminOnly");

        // Same handler the consumer app calls under /api/v1/chat/question-templates —
        // mirrors how GetDistricts is mapped under both /admin/districts and
        // /listings/locations/districts. Deactivate-not-delete: see ToggleQuestionTemplateActive.
        group.MapGet("/question-templates", ChatHandlers.GetQuestionTemplates);
        group.MapPost("/question-templates", ChatHandlers.CreateQuestionTemplate).RequireAuthorization("AdminOnly");
        group.MapPut("/question-templates/{id:guid}", ChatHandlers.UpdateQuestionTemplate).RequireAuthorization("AdminOnly");
        group.MapPut("/question-templates/{id:guid}/active", ChatHandlers.ToggleQuestionTemplateActive).RequireAuthorization("AdminOnly");

        return group;
    }
}
