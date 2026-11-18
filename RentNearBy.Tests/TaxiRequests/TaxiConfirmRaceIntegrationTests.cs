using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// P1 acceptance: two confirms for overlapping dates of one driver can never both succeed. Real PostgreSQL only.
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

    [IntegrationFact]
    public async Task Two_confirms_for_overlapping_dates_one_wins_one_gets_dates_already_booked()
    {
        await using var h = new JourneyHarness(Db);
        var first = await TaxiOfferKit.SeedAsync(h, RequestStates.Selected, 1, accepted: 1);
        var driver = first.Drivers[0];
        var firstOffer = await TaxiOfferKit.AddOfferAsync(h, first.RequestId, driver, OfferStates.Selected, 1, confirmBy: DateTime.UtcNow.AddMinutes(10), select: true);

        // A second request from another traveller whose dates overlap the first one by a day, offered to the same driver.
        var second = await TaxiOfferKit.SeedAsync(h, RequestStates.Selected, 0, accepted: 1, startOffsetDays: 12);
        var secondOffer = await TaxiOfferKit.AddOfferAsync(h, second.RequestId, driver, OfferStates.Selected, 1, confirmBy: DateTime.UtcNow.AddMinutes(10), select: true);
        Assert.True(new TaxiDateRange(first.From, first.To).Overlaps(new TaxiDateRange(second.From, second.To)));

        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var s1 = h.NewScope();
        await using var s2 = h.NewScope();
        var k1 = TaxiOfferKit.For(s1.Db);
        var k2 = TaxiOfferKit.For(s2.Db);

        var a = Task.Run(() => k1.Bookings.ConfirmAsync(firstOffer, driver.User, new HoldingCalendar(new EfDriverCalendarSql(s1.Db), locked, TimeSpan.FromSeconds(1)), CancellationToken.None));
        await locked.Task;
        var b = Task.Run(() => k2.Bookings.ConfirmAsync(secondOffer, driver.User, new EfDriverCalendarSql(s2.Db), CancellationToken.None));
        var results = await Task.WhenAll(a, b);

        Assert.True(results[0].Ok);
        Assert.Equal(TaxiRequestErrorTypes.DatesAlreadyBooked, results[1].Error);
        await using var read = CreateContext();
        var active = await read.Set<TaxiBooking>().AsNoTracking()
            .Where(x => x.DriverListingId == driver.Listing && BookingStates.Active.Contains(x.Status)).ToListAsync();
        Assert.Single(active);
        Assert.Equal(first.RequestId, active[0].RequestId);
    }
}
