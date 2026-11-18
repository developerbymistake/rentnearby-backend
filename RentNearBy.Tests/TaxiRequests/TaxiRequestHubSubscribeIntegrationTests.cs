using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one. Drives the hub method with the real context.
[Trait("Category", "Integration")]
public class TaxiRequestHubSubscribeIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static NotificationHub HubFor(Guid userId, IGroupManager groups)
        => new() { Context = new TaxiRequestEventsTests.CallerContext("c1", TaxiRequestEventsTests.Caller(userId)), Groups = groups };

    [IntegrationFact]
    public async Task Traveller_joins_the_group_of_their_own_request()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 0);
        var groups = new TaxiRequestEventsTests.GroupManager();
        await using var s = h.NewScope();

        await HubFor(seed.TravellerId, groups).SubscribeTaxiRequest(seed.RequestId.ToString(), s.Db);

        Assert.Equal([("c1", $"taxi_request_{seed.RequestId:D}")], groups.Added);
    }

    [IntegrationFact]
    public async Task Another_user_or_an_unknown_id_joins_nothing()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 0);
        var stranger = await h.AddSeekerAsync("Stranger");
        var groups = new TaxiRequestEventsTests.GroupManager();
        await using var s = h.NewScope();

        await HubFor(stranger, groups).SubscribeTaxiRequest(seed.RequestId.ToString(), s.Db);
        await HubFor(seed.TravellerId, groups).SubscribeTaxiRequest(Guid.NewGuid().ToString(), s.Db);

        Assert.Empty(groups.Added);
    }
}
