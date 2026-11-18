using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Security review of phase B3: select of a driver booked elsewhere, client id reused for another trip.
public class TaxiRequestReviewB3FixTests
{
    private static string Infra(params string[] parts) => TaxiSource.Read(["RentNearBy.Infrastructure", "Services", "TaxiRequests", .. parts]);

    [Fact]
    public void Select_closes_the_offer_of_a_driver_booked_on_overlapping_dates_before_selecting()
    {
        var select = TaxiSource.Member(Infra("TaxiRequestService.cs"), "public async Task<TaxiRequestResult> SelectAsync(");
        var calendar = TaxiSource.At(select, "DriverCalendar.ConflictForAsync(db, offer.DriverListingId, locked.DateFrom, locked.DateTo, c)");
        Assert.True(calendar > TaxiSource.At(select, "return await RunAsync(requestId,"), "The check runs under the request lock");
        Assert.True(calendar < TaxiSource.At(select, "OfferStates.Selected, null, null, c)"), "The check runs before the offer becomes Selected");
        Assert.Contains("OfferEndReasons.Booked", select);
    }

    [Fact]
    public void Client_id_of_another_trip_is_a_bad_request_not_already_active()
    {
        var trip = Guid.NewGuid();
        var ended = new TaxiRequest { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), Status = RequestStates.Cancelled };

        var other = TaxiRequestService.ReplayOrConflict(ended, trip);
        Assert.Equal((400, TaxiRequestCreateRules.BadRequestType), (other.Error!.Status, other.Error.Type));
        Assert.Equal(TaxiRequestService.ClientIdReusedMessage, other.Error.Message);
        Assert.Null(other.Error.Data);

        var same = TaxiRequestService.ReplayOrConflict(ended, ended.TripId);
        Assert.True(same.IsOk);
        Assert.Same(ended, same.Request);
    }
}

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class TaxiRequestReviewB3FixIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static TaxiRequestService Service(ApplicationDbContext db, TaxiOfferKit.Kit kit)
        => new(db, kit.Store, kit.Places, new DueActionScheduler(), kit.Pushes, new InMemoryRateLimitService(), NullLogger<TaxiRequestService>.Instance);

    [IntegrationFact]
    public async Task Select_of_a_driver_booked_on_those_dates_closes_the_offer_and_reopens_the_request()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Collecting, 1, accepted: 1);
        var accepted = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Accepted, 1);
        var other = await TaxiOfferKit.SeedAsync(h, RequestStates.Booked, 0);
        var confirmed = await TaxiOfferKit.AddOfferAsync(h, other.RequestId, seed.Drivers[0], OfferStates.Confirmed, 1);
        await TaxiOfferKit.AddBookingAsync(h, other, confirmed, seed.Drivers[0], BookingStates.Booked, seed.From, seed.To);

        TaxiRequestResult result;
        await using (var s = h.NewScope())
            result = await Service(s.Db, TaxiOfferKit.For(s.Db)).SelectAsync(seed.RequestId, accepted, seed.TravellerId);

        Assert.Equal(TaxiRequestErrorTypes.OfferStateInvalid, result.Error!.Type);
        await using var read = CreateContext();
        var row = await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == accepted);
        Assert.Equal((OfferStates.Closed, OfferEndReasons.Booked), (row.Status, row.EndReason));
        var request = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.Equal(RequestStates.Open, request.Status);
        Assert.Null(request.SelectedOfferId);
        Assert.False(await read.Set<DueAction>().AnyAsync(d => d.SubjectId == accepted && d.Kind == DueActionKinds.ConfirmExpire));
    }
}
