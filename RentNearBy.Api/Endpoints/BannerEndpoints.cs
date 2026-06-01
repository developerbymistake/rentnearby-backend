using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class BannerEndpoints
{
    public static RouteGroupBuilder MapBannerEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/banners/active", BannerHandlers.GetActiveBanner).RequireAuthorization();
        group.MapPost("/banners/{id:guid}/dismiss", BannerHandlers.DismissBanner).RequireAuthorization();
        return group;
    }

    public static RouteGroupBuilder MapAdminBannerEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/district-banners", BannerHandlers.AdminGetAllBanners).RequireAuthorization("AdminOnly");
        group.MapPost("/district-banners", BannerHandlers.AdminCreateBanner).RequireAuthorization("AdminOnly").DisableAntiforgery();
        group.MapPut("/district-banners/{id:guid}/active", BannerHandlers.AdminToggleBanner).RequireAuthorization("AdminOnly");
        group.MapDelete("/district-banners/{id:guid}", BannerHandlers.AdminDeleteBanner).RequireAuthorization("AdminOnly");
        return group;
    }
}
