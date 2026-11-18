using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RentNearBy.Api.Extensions;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;
using AppServiceRegistration = RentNearBy.Api.Extensions.ServiceCollectionExtensions;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiRequestDiTests
{
    private static IServiceCollection Registered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "Host=localhost;Database=test;Username=test;Password=test",
                ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(configuration);
        return services;
    }

    private static readonly Type[] Singletons =
    [
        typeof(TaxiRequestMetrics), typeof(DueActionsSignal), typeof(IDueActionsSignal), typeof(IDueActionsWaiter),
        typeof(MatcherSignal), typeof(IMatcherSignal), typeof(TaxiAckTokens),
        typeof(IDueActionScheduler), typeof(ITaxiPushWriter), typeof(ITaxiRequestEvents),
    ];

    private static readonly Type[] Scoped =
    [
        typeof(TaxiRequestStore), typeof(TaxiPickupPlaces), typeof(ITripPlanLockReader), typeof(ITaxiRequestClosure), typeof(TaxiRequestClosure),
        typeof(TaxiRequestService), typeof(TaxiOfferService), typeof(TaxiBookingService),
        typeof(IMatcherPassUnitFactory), typeof(TaxiMatcherPass),
    ];

    private static ServiceProvider Build() =>
        Registered().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });

    [Fact]
    public void Every_taxi_request_service_resolves()
    {
        using var provider = Build();
        foreach (var type in Singletons)
            Assert.NotNull(provider.GetRequiredService(type));
        Assert.Same(provider.GetRequiredService<DueActionsSignal>(), provider.GetRequiredService<IDueActionsSignal>());
        Assert.Same(provider.GetRequiredService<MatcherSignal>(), provider.GetRequiredService<IMatcherSignal>());

        using var scope = provider.CreateScope();
        foreach (var type in Scoped)
            Assert.NotNull(scope.ServiceProvider.GetRequiredService(type));
        Assert.Equal(DueActionKinds.All.Count, scope.ServiceProvider.GetServices<IDueActionHandler>().Count());
        Assert.Same(scope.ServiceProvider.GetRequiredService<TaxiRequestClosure>(), scope.ServiceProvider.GetRequiredService<ITaxiRequestClosure>());
    }

    [Fact]
    public void Every_due_action_kind_has_exactly_one_handler()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var kinds = scope.ServiceProvider.GetServices<IDueActionHandler>().Select(h => h.Kind).ToList();
        foreach (var kind in DueActionKinds.All)
            Assert.Single(kinds, k => k == kind);
        Assert.Equal(DueActionKinds.All.Count, kinds.Count);
    }

    [Fact]
    public void Workers_are_registered_once()
    {
        var hosted = Registered()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        Assert.Single(hosted, t => t == typeof(DueActionsWorker));
        Assert.Single(hosted, t => t == typeof(TaxiMatcherService));
    }
}
