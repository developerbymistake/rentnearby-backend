using Microsoft.Extensions.DependencyInjection.Extensions;
using RentNearBy.Api.Extensions.Owner;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner;

namespace RentNearBy.Api.Extensions;

// Owner roles: Stay Owner and Taxi Driver (CONTRACT_BACKEND section 12). Seam defaults are TryAdd; each lane replaces its own
// in its OwnerBeNRegistration file. IOwnerCalendarSync and IPlanReconcileSignal are singletons (the save interceptor and the
// payment switch outlive a request); the others are scoped.
public static class OwnerFeatureExtensions
{
    public static IServiceCollection AddOwnerFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OwnerFeatureOptions>(configuration.GetSection(OwnerFeatureOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IOwnerCalendarSync, NullOwnerCalendarSync>();
        services.TryAddSingleton<IPlanReconcileSignal, NullPlanReconcileSignal>();
        services.TryAddScoped<IOwnerNotifier, NullOwnerNotifier>();
        services.TryAddScoped<IOwnerListingPurge, NullOwnerListingPurge>();
        services.TryAddScoped<IStayDatedPricing, NullStayDatedPricing>();
        services.TryAddScoped<IStayDetailPricing, NullStayDetailPricing>();
        services.TryAddScoped<OwnerContextReader>();

        services.AddOwnerBe1();
        services.AddOwnerBe2();
        services.AddOwnerBe3();
        services.AddOwnerBe4();
        services.AddStayQuotation();
        return services;
    }

    public static IEndpointRouteBuilder MapOwnerFeature(this IEndpointRouteBuilder app)
    {
        app.MapOwnerBe1();
        app.MapOwnerBe2();
        app.MapOwnerBe3();
        app.MapOwnerBe4();
        app.MapStayQuotation();
        return app;
    }
}
