using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// P1 acceptance and owner row O9: two confirms for overlapping dates of one driver can never both succeed; the first
// confirm wins and the second gets DatesAlreadyBooked, which lapses its selection at once (O8). Real PostgreSQL only.
[Trait("Category", "Integration")]
public class TaxiConfirmRaceIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    // Holds the driver row lock for a while after taking it, so the second confirm reaches the lock while the first is
    // still inside its transaction.
    private sealed class HoldingCalendar(IDriverCalendarSql inner, TaskCompletionSource locked, TimeSpan hold) : IDriverCalendarSql
    {
        public async Task<DriverLockRow?> LockDriverAsync(Guid driverListingId, CancellationToken ct)
        {
            var row = await inner.LockDriverAsync(driverListingId, ct);
            locked.TrySetResult();
            await Task.Delay(hold, ct);
            return row;
        }

        public Task<TaxiDateRange?> FindOverlapAsync(Guid driverListingId, TaxiDateRange range, Guid? exceptEnquiryId, CancellationToken ct)
            => inner.FindOverlapAsync(driverListingId, range, exceptEnquiryId, ct);
    }

    // Both confirms wait here, each already holding its own request row lock, and go for the driver row lock together.
    private sealed class StartLineCalendar(IDriverCalendarSql inner, Barrier start) : IDriverCalendarSql
    {
        private int _started;

        public async Task<DriverLockRow?> LockDriverAsync(Guid driverListingId, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
                Assert.True(await Task.Run(() => start.SignalAndWait(TimeSpan.FromSeconds(30), ct), ct), "the other confirm never reached the start line");
            return await inner.LockDriverAsync(driverListingId, ct);
        }

        public Task<TaxiDateRange?> FindOverlapAsync(Guid driverListingId, TaxiDateRange range, Guid? exceptEnquiryId, CancellationToken ct)
            => inner.FindOverlapAsync(driverListingId, range, exceptEnquiryId, ct);
    }

    private sealed record Race(TaxiOfferKit.Seed First, Guid FirstOffer, TaxiOfferKit.Seed Second, Guid SecondOffer, (Guid Listing, Guid User) Driver);

    // Two travellers chose the same driver for dates that overlap by a day: two pending selections at once (O9 allows it).
    private static async Task<Race> TwoSelectionsAsync(JourneyHarness h)
    {
        var first = await TaxiOfferKit.SeedAsync(h, RequestStates.Selected, 1, accepted: 1);
        var driver = first.Drivers[0];
        var firstOffer = await TaxiOfferKit.AddOfferAsync(h, first.RequestId, driver, OfferStates.Selected, 1, confirmBy: DateTime.UtcNow.AddMinutes(10), select: true);
        var second = await TaxiOfferKit.SeedAsync(h, RequestStates.Selected, 0, accepted: 1, startOffsetDays: 12);
        var secondOffer = await TaxiOfferKit.AddOfferAsync(h, second.RequestId, driver, OfferStates.Selected, 1, confirmBy: DateTime.UtcNow.AddMinutes(10), select: true);
        Assert.True(new TaxiDateRange(first.From, first.To).Overlaps(new TaxiDateRange(second.From, second.To)));
        return new Race(first, firstOffer, second, secondOffer, driver);
    }

    private async Task AssertOneBookingAndLapsedLoserAsync(Race race, Guid winnerRequest, Guid loserRequest, Guid loserOffer)
    {
        await using var read = CreateContext();
        var active = await read.Set<TaxiBooking>().AsNoTracking()
            .Where(x => x.DriverListingId == race.Driver.Listing && BookingStates.Active.Contains(x.Status)).ToListAsync();
        Assert.Equal(winnerRequest, Assert.Single(active).RequestId);

        var loser = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == loserRequest);
        Assert.Equal((RequestStates.Open, (Guid?)null), (loser.Status, loser.SelectedOfferId));
        var offer = await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == loserOffer);
        Assert.Equal((OfferStates.Closed, OfferEndReasons.Booked), (offer.Status, offer.EndReason));
        Assert.False(await read.Set<TaxiBooking>().AnyAsync(b => b.RequestId == loserRequest));
        Assert.Equal(RequestStates.Booked, (await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == winnerRequest)).Status);
    }

    [IntegrationFact]
    public async Task Two_confirms_for_overlapping_dates_one_wins_one_gets_dates_already_booked()
    {
        await using var h = new JourneyHarness(Db);
        var race = await TwoSelectionsAsync(h);

        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var s1 = h.NewScope();
        await using var s2 = h.NewScope();
        var k1 = TaxiOfferKit.For(s1.Db);
        var k2 = TaxiOfferKit.For(s2.Db, new TaxiOfferKit.RecordingPushes());

        var a = Task.Run(() => k1.Bookings.ConfirmAsync(race.FirstOffer, race.Driver.User, new HoldingCalendar(new EfDriverCalendarSql(s1.Db), locked, TimeSpan.FromSeconds(1)), CancellationToken.None));
        await locked.Task;
        var b = Task.Run(() => k2.Bookings.ConfirmAsync(race.SecondOffer, race.Driver.User, new EfDriverCalendarSql(s2.Db), CancellationToken.None));
        var results = await Task.WhenAll(a, b);

        Assert.True(results[0].Ok);
        Assert.Equal(TaxiRequestErrorTypes.DatesAlreadyBooked, results[1].Error);
        Assert.Contains(nameof(TaxiNoticeKind.SelectionLapsed), k2.Pushes.Sent);
        await AssertOneBookingAndLapsedLoserAsync(race, race.First.RequestId, race.Second.RequestId, race.SecondOffer);
    }

    // O9: both confirms are released at the same instant at the driver row lock; whichever gets it first books, the
    // other one gets DatesAlreadyBooked. Repeated to give the scheduler both orders.
    [IntegrationFact]
    public async Task Two_parallel_confirms_of_one_driver_exactly_one_books()
    {
        for (var round = 0; round < 5; round++)
        {
            await using var h = new JourneyHarness(Db);
            var race = await TwoSelectionsAsync(h);

            using var start = new Barrier(2);
            await using var s1 = h.NewScope();
            await using var s2 = h.NewScope();
            var k1 = TaxiOfferKit.For(s1.Db);
            var k2 = TaxiOfferKit.For(s2.Db);

            var a = Task.Run(() => k1.Bookings.ConfirmAsync(race.FirstOffer, race.Driver.User, new StartLineCalendar(new EfDriverCalendarSql(s1.Db), start), CancellationToken.None));
            var b = Task.Run(() => k2.Bookings.ConfirmAsync(race.SecondOffer, race.Driver.User, new StartLineCalendar(new EfDriverCalendarSql(s2.Db), start), CancellationToken.None));
            var results = await Task.WhenAll(a, b);

            Assert.Single(results, r => r.Ok);
            Assert.Single(results, r => r.Error == TaxiRequestErrorTypes.DatesAlreadyBooked);
            var firstWon = results[0].Ok;
            await AssertOneBookingAndLapsedLoserAsync(race,
                firstWon ? race.First.RequestId : race.Second.RequestId,
                firstWon ? race.Second.RequestId : race.First.RequestId,
                firstWon ? race.SecondOffer : race.FirstOffer);
        }
    }
}
