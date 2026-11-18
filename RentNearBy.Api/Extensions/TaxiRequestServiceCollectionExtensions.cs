using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Api.Services;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

namespace RentNearBy.Api.Extensions;

public static class TaxiRequestServiceCollectionExtensions
{
    // Taxi request system: route city keys, due actions, matcher, request, offer and booking services.
    public static IServiceCollection AddTaxiRequests(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITaxiRouteCityKeyStore, TaxiRouteCityKeyStore>();
        // Fills null taxi route city keys: once 2 min after start, then nightly at 00:20 IST.
        services.AddHostedService<TaxiRouteCityBackfillService>();

        services.AddSingleton<TaxiRequestMetrics>();
        services.AddSingleton<DueActionsSignal>();
        services.AddSingleton<IDueActionsSignal>(sp => sp.GetRequiredService<DueActionsSignal>());
        services.AddSingleton<IDueActionsWaiter>(sp => sp.GetRequiredService<DueActionsSignal>());
        services.AddSingleton<MatcherSignal>();
        services.AddSingleton<IMatcherSignal>(sp => sp.GetRequiredService<MatcherSignal>());
        services.AddSingleton(_ => new TaxiAckTokens(configuration));
        services.AddSingleton<IDueActionScheduler, DueActionScheduler>();
        services.AddSingleton<ITaxiPushWriter, TaxiPushWriter>();
        // The Status screen hint goes through the notification hub; AddSignalR is idempotent, Program.cs adds it too.
        services.AddSignalR();
        services.AddSingleton<ITaxiRequestEvents, TaxiRequestEvents>();

        services.AddScoped<TaxiRequestStore>();
        services.AddScoped<TaxiPickupPlaces>();
        services.AddScoped<TaxiRequestViews>();
        services.AddScoped<ITripPlanLockReader, TripPlanLockReader>();
        // Concrete type too: TripEnquiryClosure and the availability route take TaxiRequestClosure itself.
        services.AddScoped<TaxiRequestClosure>();
        services.AddScoped<ITaxiRequestClosure>(sp => sp.GetRequiredService<TaxiRequestClosure>());
        services.AddScoped<TaxiRequestService>();
        services.AddScoped<TaxiOfferService>();
        services.AddScoped<TaxiBookingService>();
        services.AddScoped<IMatcherPassUnitFactory, PostgresMatcherPassUnitFactory>();
        services.AddScoped<TaxiMatcherPass>();

        services.AddScoped<IDueActionHandler, OfferExpireHandler>();
        services.AddScoped<IDueActionHandler, ConfirmExpireHandler>();
        services.AddScoped<IDueActionHandler, RequestWaitExpireHandler>();
        services.AddScoped<IDueActionHandler, RequestStaleExpireHandler>();
        services.AddScoped<IDueActionHandler, BookingStartHandler>();
        services.AddScoped<IDueActionHandler, BookingAutoCompleteHandler>();

        services.AddHostedService<DueActionsWorker>();
        services.AddHostedService<TaxiMatcherService>();
        return services;
    }

    // Contract sections 5, 6, 7 and 11. T1 to T3 sit on a /trips group that keeps the planner gate; the per-route master
    // switch (TripExecutionGateFilter) is added inside the endpoint files.
    public static IEndpointRouteBuilder MapTaxiRequests(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/trips")
            .WithTags("Trips")
            .AddEndpointFilter<TripPlannerGateFilter>()
            .MapTripTaxiRequestEndpoints();

        app.MapGroup("/api/v1/taxi-requests")
            .WithTags("TaxiRequests")
            .MapTaxiRequestEndpoints()
            .MapTaxiDriverRequestEndpoints();

        app.MapGroup("/api/v1/admin")
            .WithTags("AdminTaxiRequests")
            .MapAdminTaxiRequestEndpoints();

        return app;
    }
}
