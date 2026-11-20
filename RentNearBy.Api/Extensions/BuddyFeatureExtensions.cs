using Microsoft.Extensions.DependencyInjection.Extensions;
using RentNearBy.Api.Extensions.Buddy;
using RentNearBy.Api.Services;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy;

namespace RentNearBy.Api.Extensions;

// Buddies, trip sharing and expenses. Seam defaults are TryAdd; each lane replaces its own in its Be*Registration file.
public static class BuddyFeatureExtensions
{
    public static IServiceCollection AddBuddyFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BuddyFeatureOptions>(configuration.GetSection(BuddyFeatureOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<INoticeWriter, NullNoticeWriter>();
        services.TryAddScoped<ITripMoneyCards, NullTripMoneyCards>();
        services.TryAddScoped<ITripMoneyGuard, NullTripMoneyGuard>();
        services.TryAddScoped<ITripSharingHooks, NullTripSharingHooks>();

        services.AddScoped<ITripLedger, TripLedger>();
        services.TryAddScoped<ITripAccess, TripAccess>();
        services.AddSignalR();
        services.AddSingleton<IBuddyLiveHints, BuddyLiveHints>();
        services.AddHostedService<BuddyFeatureJobsService>();

        services.AddBe1Buddies();
        services.AddBe2Sharing();
        services.AddBe3Expenses();
        services.AddBe4PaymentsAndNotices();
        return services;
    }

    public static IEndpointRouteBuilder MapBuddyFeature(this IEndpointRouteBuilder app)
    {
        app.MapBe1Buddies();
        app.MapBe2Sharing();
        app.MapBe3Expenses();
        app.MapBe4PaymentsAndNotices();
        return app;
    }
}
