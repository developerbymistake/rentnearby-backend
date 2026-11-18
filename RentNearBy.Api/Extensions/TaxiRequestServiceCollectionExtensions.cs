using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Api.Extensions;

public static class TaxiRequestServiceCollectionExtensions
{
    // Taxi request system. Route city keys now; dispatch and its workers are added by later packages.
    public static IServiceCollection AddTaxiRequests(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITaxiRouteCityKeyStore, TaxiRouteCityKeyStore>();
        // Fills null taxi route city keys: once 2 min after start, then nightly at 00:20 IST.
        services.AddHostedService<TaxiRouteCityBackfillService>();
        return services;
    }
}
