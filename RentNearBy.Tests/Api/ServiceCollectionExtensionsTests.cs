using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RentNearBy.Api.Extensions;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Retention;
using AppServiceRegistration = RentNearBy.Api.Extensions.ServiceCollectionExtensions;

namespace RentNearBy.Tests.Api;

public class ServiceCollectionExtensionsTests
{
    private static IServiceCollection Registered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "Host=localhost;Database=test;Username=test;Password=test",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(configuration);
        return services;
    }

    [Theory]
    [InlineData(typeof(RetentionRunner))]
    [InlineData(typeof(DeletedAccountRecordPurgeService))]
    [InlineData(typeof(TripTimerService))]
    [InlineData(typeof(JourneyWatchService))]
    [InlineData(typeof(PlanLifecycleService))]
    [InlineData(typeof(OutboxRelayService))]
    [InlineData(typeof(PushDeliveryWorkerService))]
    [InlineData(typeof(TripReminderService))]
    [InlineData(typeof(NotificationPushWorkerService))]
    public void TimedJob_IsRegisteredAsHostedService(Type worker)
    {
        var hosted = Registered()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        Assert.Single(hosted, t => t == worker);
    }

    [Theory]
    [InlineData("Cancel", PlayServerActions.Cancel)]
    [InlineData("cancel", PlayServerActions.Cancel)]
    [InlineData(" CANCEL ", PlayServerActions.Cancel)]
    [InlineData("RevokeProrated", PlayServerActions.RevokeProrated)]
    [InlineData(" RevokeProrated ", PlayServerActions.RevokeProrated)]
    [InlineData("revokeprorated", PlayServerActions.RevokeProrated)]
    [InlineData("RevokeFull", null)]
    [InlineData("Refund", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormalizeRoleChangeAction_OnlyCancelOrRevokeProrated(string? value, string? expected)
        => Assert.Equal(expected, AppServiceRegistration.NormalizeRoleChangeAction(value));
}
