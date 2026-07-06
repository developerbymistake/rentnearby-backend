using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class PlotListingEndpoints
{
    public static RouteGroupBuilder MapPlotListingEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/plans", PlotListingHandlers.GetPublicPlotPlans);
        group.MapGet("/context", PlotListingHandlers.GetContext);
        group.MapGet("/nearby", PlotListingHandlers.GetNearby);
        group.MapGet("/{id:guid}", PlotListingHandlers.GetById);

        group.MapGet("/my", PlotListingHandlers.GetMyPlotListings).RequireAuthorization();
        group.MapPost("/", PlotListingHandlers.CreatePlotListing).RequireAuthorization().DisableAntiforgery();
        group.MapPut("/{id:guid}", PlotListingHandlers.UpdatePlotListing).RequireAuthorization();
        group.MapDelete("/{id:guid}", PlotListingHandlers.DeletePlotListing).RequireAuthorization();

        group.MapPost("/{id:guid}/photos", PlotListingHandlers.UploadPhoto).RequireAuthorization().DisableAntiforgery();
        group.MapDelete("/{id:guid}/photos/{photoId:guid}", PlotListingHandlers.DeletePhoto).RequireAuthorization();

        group.MapPost("/{id:guid}/report", PlotListingHandlers.ReportPlotListing).RequireAuthorization();

        group.MapGet("/payment/status", PlotListingHandlers.GetPlotMembershipStatus).RequireAuthorization();
        group.MapPost("/{plotId:guid}/create-order", PlotListingHandlers.CreatePlotListingOrder).RequireAuthorization();
        group.MapPost("/{plotId:guid}/verify-payment", PlotListingHandlers.VerifyPlotListingPayment).RequireAuthorization();
        group.MapPost("/upgrade-plan/create-order", PlotListingHandlers.CreatePlotListingUpgradeOrder).RequireAuthorization();
        group.MapPost("/upgrade-plan/verify", PlotListingHandlers.VerifyPlotListingUpgradePayment).RequireAuthorization();

        return group;
    }

    public static IEndpointRouteBuilder MapAdminPlotListingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", PlotListingHandlers.GetAdminPlotListings).RequireAuthorization("AdminOnly");
        app.MapGet("/{id:guid}", PlotListingHandlers.GetAdminPlotById).RequireAuthorization("AdminOnly");
        app.MapPut("/{id:guid}", PlotListingHandlers.AdminTogglePlotListing).RequireAuthorization("AdminOnly");
        app.MapDelete("/{id:guid}", PlotListingHandlers.AdminDeletePlotListing).RequireAuthorization("AdminOnly");
        return app;
    }
}
