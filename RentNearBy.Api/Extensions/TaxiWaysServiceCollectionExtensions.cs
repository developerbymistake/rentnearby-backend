using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Api.Extensions;

public static class TaxiWaysServiceCollectionExtensions
{
    // Ways to go (network, search), saved journeys, route road lines and the journey watch.
    public static IServiceCollection AddTaxiWays(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TaxiWaysOptions>(configuration.GetSection("TaxiWays"));
        services.AddSingleton<ITaxiWaysNetwork, TaxiWaysNetwork>();
        services.AddScoped<ITaxiWaysNetworkSource, TaxiWaysNetworkRepository>();
        services.AddScoped<ITaxiWaysSearch, TaxiWaysSearch>();
        services.AddScoped<ISavedJourneyRepository, SavedJourneyRepository>();
        services.AddScoped<ITaxiRouteGeometryRepository, TaxiRouteGeometryRepository>();
        services.AddHostedService<TaxiRouteGeometryService>();
        services.AddHostedService<JourneyWatchService>();
        return services;
    }
}
