using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class PlotEndpoints
{
    public static RouteGroupBuilder MapPlotEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/plans", PlotHandlers.GetPublicPlotPlans);
        group.MapGet("/context", PlotHandlers.GetContext);
        group.MapGet("/nearby", PlotHandlers.GetNearby);
        group.MapGet("/{id:guid}", PlotHandlers.GetById);

        group.MapGet("/my", PlotHandlers.GetMyPlots).RequireAuthorization();
        group.MapPost("/", PlotHandlers.CreatePlot).RequireAuthorization().DisableAntiforgery();
        group.MapPut("/{id:guid}", PlotHandlers.UpdatePlot).RequireAuthorization();
        group.MapDelete("/{id:guid}", PlotHandlers.DeletePlot).RequireAuthorization();

        group.MapPost("/{id:guid}/photos", PlotHandlers.UploadPhoto).RequireAuthorization().DisableAntiforgery();
        group.MapDelete("/{id:guid}/photos/{photoId:guid}", PlotHandlers.DeletePhoto).RequireAuthorization();

        group.MapGet("/payment/status", PlotHandlers.GetPlotMembershipStatus).RequireAuthorization();
        group.MapPost("/{plotId:guid}/create-order", PlotHandlers.CreatePlotOrder).RequireAuthorization();
        group.MapPost("/{plotId:guid}/verify-payment", PlotHandlers.VerifyPlotPayment).RequireAuthorization();
        group.MapPost("/upgrade-plan/create-order", PlotHandlers.CreatePlotUpgradeOrder).RequireAuthorization();
        group.MapPost("/upgrade-plan/verify", PlotHandlers.VerifyPlotUpgradePayment).RequireAuthorization();

        return group;
    }

    public static IEndpointRouteBuilder MapAdminPlotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", PlotHandlers.GetAdminPlots).RequireAuthorization("AdminOnly");
        app.MapPut("/{id:guid}", PlotHandlers.AdminTogglePlot).RequireAuthorization("AdminOnly");
        app.MapDelete("/{id:guid}", PlotHandlers.AdminDeletePlot).RequireAuthorization("AdminOnly");
        return app;
    }
}
