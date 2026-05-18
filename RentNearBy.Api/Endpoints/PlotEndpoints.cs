using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class PlotEndpoints
{
    public static RouteGroupBuilder MapPlotEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/context", PlotHandlers.GetContext);
        group.MapGet("/nearby", PlotHandlers.GetNearby);
        group.MapGet("/{id:guid}", PlotHandlers.GetById);

        group.MapGet("/my", PlotHandlers.GetMyPlots).RequireAuthorization();
        group.MapPost("/", PlotHandlers.CreatePlot).RequireAuthorization().DisableAntiforgery();
        group.MapPut("/{id:guid}", PlotHandlers.UpdatePlot).RequireAuthorization();
        group.MapDelete("/{id:guid}", PlotHandlers.DeletePlot).RequireAuthorization();

        group.MapPost("/{id:guid}/photos", PlotHandlers.UploadPhoto).RequireAuthorization().DisableAntiforgery();
        group.MapDelete("/{id:guid}/photos/{photoId:guid}", PlotHandlers.DeletePhoto).RequireAuthorization();

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
