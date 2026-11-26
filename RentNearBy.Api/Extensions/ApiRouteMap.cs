using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Hubs;

namespace RentNearBy.Api.Extensions;

// Every /api/v1 group, the feature routes and the hubs, mapped once here so Program.cs and the route table tests map the same table.
public static class ApiRouteMap
{
    public static IEndpointRouteBuilder MapApiRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/auth")
            .WithTags("Authentication")
            .MapAuthEndpoints();

        app.MapGroup("/api/v1/admin-auth")
            .WithTags("AdminAuth")
            .MapAdminAuthEndpoints();

        app.MapGroup("/api/v1/users")
            .WithTags("Users")
            .MapUsersEndpoints();

        app.MapGroup("/api/v1/roles")
            .WithTags("Roles")
            .MapRolesEndpoints();

        app.MapGroup("/api/v1/locations")
            .WithTags("Locations")
            .MapLocationsEndpoints();

        app.MapGroup("/api/v1/admin")
            .WithTags("Admin")
            .MapAdminEndpoints();

        app.MapGroup("/api/v1/stays")
            .WithTags("Stays")
            .AddEndpointFilter(new TabGateFilter(RentNearBy.Core.Models.AppTabKeys.Stays))
            .MapStayEndpoints();

        app.MapGroup("/api/v1/admin/stays")
            .WithTags("AdminStays")
            .MapAdminStayEndpoints();

        app.MapGroup("/api/v1/stay-jobs")
            .WithTags("StayJobs")
            .AddEndpointFilter(new TabGateFilter(RentNearBy.Core.Models.AppTabKeys.Jobs, RentNearBy.Core.Models.AppTabKeys.Stays))
            .MapStayJobEndpoints();

        app.MapGroup("/api/v1/admin/stay-jobs")
            .WithTags("AdminStayJobs")
            .MapAdminStayJobEndpoints();

        app.MapGroup("/api/v1/taxis")
            .WithTags("Taxis")
            .AddEndpointFilter(new TabGateFilter(RentNearBy.Core.Models.AppTabKeys.Taxi))
            .MapTaxiEndpoints();

        app.MapGroup("/api/v1/admin/taxis")
            .WithTags("AdminTaxis")
            .MapAdminTaxiEndpoints();

        app.MapGroup("/api/v1/account")
            .WithTags("Account")
            .MapAccountEndpoints();

        app.MapGroup("/api/v1/notifications")
            .WithTags("Notifications")
            .MapNotificationEndpoints();

        app.MapGroup("/api/v1/payments")
            .WithTags("Payments")
            .MapPaymentEndpoints();

        app.MapGroup("/api/v1")
            .WithTags("Banners")
            .MapBannerEndpoints();

        app.MapGroup("/api/v1/admin")
            .WithTags("AdminBanners")
            .MapAdminBannerEndpoints();

        app.MapGroup("/api/v1/chat")
            .WithTags("Chat")
            .MapChatEndpoints();

        app.MapGroup("/api/v1/home")
            .WithTags("Home")
            .MapHomeEndpoints();

        app.MapGroup("/api/v1/config")
            .WithTags("Config")
            .MapConfigEndpoints();

        app.MapGroup("/api/v1/plans")
            .WithTags("Plans")
            .MapPlanEndpoints();

        app.MapGroup("/api/v1/trips")
            .WithTags("Trips")
            .AddEndpointFilter<TripPlannerGateFilter>()
            .AddEndpointFilter<TripPlanPauseFilter>()
            .MapTripEndpoints();

        app.MapTaxiRequests();
        app.MapBuddyFeature();
        app.MapSocialFeature();
        app.MapOwnerFeature();

        app.MapHub<BannerHub>("/hubs/banner");
        app.MapHub<ChatHub>("/hubs/chat");
        app.MapHub<NotificationHub>("/hubs/notification");

        return app;
    }
}
