using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class ConfigEndpoints
{
    public static RouteGroupBuilder MapConfigEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/listing-limits", ConfigHandlers.GetListingLimits).AllowAnonymous();
        group.MapGet("/payment-feature", ConfigHandlers.GetPaymentFeature).AllowAnonymous();
        group.MapGet("/app-tabs", ConfigHandlers.GetAppTabs).AllowAnonymous();
        group.MapGet("/app-version", ConfigHandlers.GetAppVersion).AllowAnonymous();
        return group;
    }
}
