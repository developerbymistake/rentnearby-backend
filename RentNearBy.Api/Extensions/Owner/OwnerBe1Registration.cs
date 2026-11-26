using RentNearBy.Api.Endpoints.Owner;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;

namespace RentNearBy.Api.Extensions.Owner;

// Lane BE1 only. calendar: C1-C11, K1-K4, money notes, Auto hooks (IOwnerCalendarSync singleton), repair job, retention rules, listing purge (IOwnerListingPurge).
// Services: register here (a plain Add* after the foundation's TryAdd default wins). Routes: map here; new owner groups carry no TabGateFilter.
// The six retention rules (OwnerRetentionRules) are found by the existing assembly scan of IRetentionRuleSource.
public static class OwnerBe1Registration
{
    public static IServiceCollection AddOwnerBe1(this IServiceCollection services)
    {
        services.AddSingleton<IOwnerCalendarSync, OwnerCalendarSync>();
        services.AddScoped<IOwnerListingPurge, OwnerListingPurge>();
        services.AddScoped<OwnerCalendarReader>();
        services.AddScoped<OwnerCalendarWriter>();
        services.AddScoped<MoneyNoteService>();
        services.AddScoped<BlockedRangeService>();
        services.AddScoped<TaxiDayOffService>();
        services.AddScoped<RentNearBy.Infrastructure.Services.PopularRouteReader>();
        services.AddScoped<RentNearBy.Infrastructure.Services.PopularRouteAdminStore>();
        services.AddScoped<OwnerCalendarRepairJob>();
        services.AddScoped<IBuddyFeatureJob>(sp => sp.GetRequiredService<OwnerCalendarRepairJob>());
        services.AddScoped<IBuddyFeatureJob, OwnerCalendarRepairNightlyJob>();
        return services;
    }

    public static IEndpointRouteBuilder MapOwnerBe1(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/stays/me").WithTags("Stay owner").RequireAuthorization("CanListStays").MapStayCalendarEndpoints();
        app.MapGroup("/api/v1/taxis/me").WithTags("Taxi driver").RequireAuthorization("CanListTaxis").MapTaxiCalendarEndpoints().MapTaxiDayOffEndpoints();
        return app;
    }
}
