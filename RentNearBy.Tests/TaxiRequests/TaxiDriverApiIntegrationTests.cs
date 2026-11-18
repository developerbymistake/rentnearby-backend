using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class TaxiDriverApiIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task Another_drivers_offer_is_403()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 2);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1);
        var stranger = JourneyHarness.Principal(seed.Drivers[1].User);

        await using (var s = h.NewScope())
        {
            var detail = await TaxiDriverRequestHandlers.GetOffer(offer, stranger, s.Db, CancellationToken.None);
            Assert.Equal(403, JourneyHarness.StatusOf(detail));
            var accept = await TaxiDriverRequestHandlers.Accept(offer, stranger, s.Db, TaxiOfferKit.For(s.Db).Offers, CancellationToken.None);
            Assert.Equal(403, JourneyHarness.StatusOf(accept));
            var unknown = await TaxiDriverRequestHandlers.GetOffer(Guid.NewGuid(), stranger, s.Db, CancellationToken.None);
            Assert.Equal(404, JourneyHarness.StatusOf(unknown));
        }

        await using var read = CreateContext();
        var row = await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == offer);
        Assert.Equal(OfferStates.Ringing, row.Status);
    }

    [IntegrationFact]
    public async Task Own_ringing_offer_lists_without_contact()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 1);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1);

        await using var s = h.NewScope();
        var list = await TaxiDriverViews.LoadListAsync(s.Db, seed.Drivers[0].User, null, DateTime.UtcNow, CancellationToken.None);
        var item = Assert.Single(list.Items);
        Assert.Equal(offer, item.OfferId);
        Assert.Null(item.Traveller);
        Assert.Equal(1, list.Counts.New);
    }
}
