using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-CNT-1..3 and the FB-W1 / FB-W2 SQL cases on a real PostgreSQL (RENTNEARBY_TEST_DB). WRITTEN, NOT RUN here
// (no PostGIS on the build machine). Rows are inserted by hand; the counting runs through the real call sites
// (Complete handler, TripRepository.TryCompleteAsync) or the counter statements inside a transaction.
public class CompletedCountersJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DateOnly Today => TripExecutionRules.TodayIst(DateTime.UtcNow);

    private static async Task<Guid> PlannedTripAsync(JourneyHarness h, Guid seeker, DateOnly? endDate)
    {
        var id = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        await using var s = h.NewScope();
        await s.Db.Trips.Where(t => t.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, TripStatuses.Planned)
            .SetProperty(t => t.StartDate, endDate.HasValue ? endDate.Value.AddDays(-1) : null)
            .SetProperty(t => t.EndDate, endDate));
        return id;
    }

    private static async Task<Guid> EnquiryAsync(JourneyHarness h, Guid tripId, Guid traveller, Guid host, Guid listing,
        string status, DateOnly checkOut, string kind = TripEnquiryKinds.Stay)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.TripEnquiries.Add(new TripEnquiry
        {
            Id = id, TripId = tripId, TravellerUserId = traveller, OwnerUserId = host, Kind = kind, ListingId = listing,
            ListingName = "Lake View Stay", CheckIn = checkOut.AddDays(-1), CheckOut = checkOut, Status = status,
            ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> BookingAsync(JourneyHarness h, Guid tripId, Guid traveller, Guid driver, Guid listing,
        string status, bool started)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiBooking>().Add(new TaxiBooking
        {
            Id = id, RequestId = Guid.NewGuid(), OfferId = Guid.NewGuid(), TripId = tripId, DriverListingId = listing,
            DriverUserId = driver, TravellerUserId = traveller, Status = status, DriverName = "Ravi", TravellerName = "Asha",
            TripName = "Trip", PickupCityName = "Nainital", VehicleName = "Dzire", Seats = 4,
            DateFrom = Today.AddDays(-3), DateTo = Today.AddDays(-1), BookedAt = DateTime.UtcNow.AddDays(-5),
            StartedAt = started ? DateTime.UtcNow.AddDays(-3) : null,
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    private async Task<(int Trips, int Bookings)> CountersAsync(Guid userId)
    {
        await using var read = CreateContext();
        var u = await read.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new { x.CompletedTripsCount, x.CompletedBookingsCount }).FirstAsync();
        return (u.CompletedTripsCount, u.CompletedBookingsCount);
    }

    private async Task<int> AutoCompleteAsync(Guid tripId)
    {
        await using var ctx = CreateContext();
        return await new TripRepository(ctx).TryCompleteAsync(tripId, DateTime.UtcNow);
    }

    private async Task CountTaxiAsync(Guid bookingId)
    {
        await using var ctx = CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await TripCompletionCounter.CountTaxiBookingInTxAsync(ctx, bookingId);
        await tx.CommitAsync();
    }

    private async Task CleanupBookingsAsync(params Guid[] bookingIds)
    {
        await using var c = CreateContext();
        await c.Set<TaxiBooking>().Where(b => bookingIds.Contains(b.Id)).ExecuteDeleteAsync();
    }

    // ---------- JT-B-CNT-1: Seeker ----------

    [IntegrationFact]
    public async Task Confirmed_stay_after_the_end_date_counts_once_and_survives_trip_delete()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var trip = await PlannedTripAsync(h, seeker, Today.AddDays(-1));
        await EnquiryAsync(h, trip, seeker, host, stay, TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-1));

        Assert.Equal(200, JourneyHarness.StatusOf(await h.CompleteAsync(trip, seeker)));
        Assert.Equal((1, 0), await CountersAsync(seeker));
        Assert.Equal((0, 1), await CountersAsync(host));

        Assert.Equal(200, JourneyHarness.StatusOf(await h.CompleteAsync(trip, seeker)));
        Assert.Equal((1, 0), await CountersAsync(seeker));
        Assert.Equal((0, 1), await CountersAsync(host));

        await h.DeleteTripAsync(trip, seeker);
        Assert.Equal((1, 0), await CountersAsync(seeker));
        Assert.Equal((0, 1), await CountersAsync(host));
    }

    [IntegrationFact]
    public async Task Two_parallel_completes_count_once()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var trip = await PlannedTripAsync(h, seeker, Today.AddDays(-2));
        await EnquiryAsync(h, trip, seeker, host, stay, TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-2));

        await Task.WhenAll(h.CompleteAsync(trip, seeker), h.CompleteAsync(trip, seeker), AutoCompleteAsync(trip));
        Assert.Equal((1, 0), await CountersAsync(seeker));
        Assert.Equal((0, 1), await CountersAsync(host));
    }

    [IntegrationFact]
    public async Task No_end_date_end_date_today_or_no_confirmed_booking_never_counts()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);

        var today = await PlannedTripAsync(h, seeker, Today);
        await EnquiryAsync(h, today, seeker, host, stay, TripEnquiryStatuses.ConfirmedByBoth, Today);
        await h.CompleteAsync(today, seeker);

        var iBooked = await PlannedTripAsync(h, seeker, Today.AddDays(-3));
        await EnquiryAsync(h, iBooked, seeker, host, stay, TripEnquiryStatuses.BookedByTraveller, Today.AddDays(-3));
        await h.CompleteAsync(iBooked, seeker);

        var none = await PlannedTripAsync(h, seeker, Today.AddDays(-3));
        await h.CompleteAsync(none, seeker);

        Assert.Equal((0, 0), await CountersAsync(seeker));
        Assert.Equal((0, 0), await CountersAsync(host));
    }

    [IntegrationFact]
    public async Task Early_completion_is_never_counted_later()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var trip = await PlannedTripAsync(h, seeker, Today.AddDays(1));
        await EnquiryAsync(h, trip, seeker, host, stay, TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(1));
        await h.CompleteAsync(trip, seeker);

        await using (var s = h.NewScope())
            await s.Db.Trips.Where(t => t.Id == trip).ExecuteUpdateAsync(u => u.SetProperty(t => t.EndDate, Today.AddDays(-8)));
        Assert.Equal(0, await AutoCompleteAsync(trip));
        Assert.Equal((0, 0), await CountersAsync(seeker));
    }

    [IntegrationFact]
    public async Task Auto_complete_job_counts_an_open_trip_after_the_end_date()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var trip = await PlannedTripAsync(h, seeker, Today.AddDays(-8));
        await EnquiryAsync(h, trip, seeker, host, stay, TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-8));

        Assert.Equal(1, await AutoCompleteAsync(trip));
        Assert.Equal((1, 0), await CountersAsync(seeker));
        Assert.Equal((0, 1), await CountersAsync(host));
    }

    [IntegrationFact]
    public async Task Taxi_booking_counts_for_the_seeker_unless_cancelled()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var taxi = await h.AddTaxiAsync(driver);

        var cancelled = await PlannedTripAsync(h, seeker, Today.AddDays(-2));
        var b1 = await BookingAsync(h, cancelled, seeker, driver, taxi, BookingStates.Cancelled, started: false);
        await h.CompleteAsync(cancelled, seeker);
        Assert.Equal((0, 0), await CountersAsync(seeker));

        var done = await PlannedTripAsync(h, seeker, Today.AddDays(-2));
        var b2 = await BookingAsync(h, done, seeker, driver, taxi, BookingStates.Completed, started: true);
        await h.CompleteAsync(done, seeker);
        Assert.Equal((1, 0), await CountersAsync(seeker));

        var lead = await PlannedTripAsync(h, seeker, Today.AddDays(-2));
        await EnquiryAsync(h, lead, seeker, driver, taxi, TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-2), TripEnquiryKinds.Taxi);
        await h.CompleteAsync(lead, seeker);
        Assert.Equal((2, 0), await CountersAsync(seeker));
        Assert.Equal((0, 1), await CountersAsync(driver)); // the confirmed lead (D-05); the request booking counts at ride end
        await CleanupBookingsAsync(b1, b2);
    }

    [IntegrationFact]
    public async Task Role_change_keeps_the_counter()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var trip = await PlannedTripAsync(h, seeker, Today.AddDays(-2));
        await EnquiryAsync(h, trip, seeker, host, stay, TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-2));
        await h.CompleteAsync(trip, seeker);

        await using (var s = h.NewScope())
            await s.Db.Users.Where(u => u.Id == seeker).ExecuteUpdateAsync(u => u.SetProperty(x => x.RoleId, UserRoles.TaxiDriverId));
        // A whole-row save of a stale entity never writes the counters (after-save behaviour Ignore).
        await using (var s = h.NewScope())
        {
            var stale = await s.Db.Users.FirstAsync(u => u.Id == seeker);
            stale.CompletedTripsCount = 0;
            s.Db.Users.Update(stale);
            await s.Db.SaveChangesAsync();
        }
        Assert.Equal((1, 0), await CountersAsync(seeker));
    }

    // ---------- JT-B-CNT-2: Stay Owner ----------

    [IntegrationFact]
    public async Task Two_hosts_in_one_trip_get_one_each_and_unconfirmed_rows_do_not_count()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var hostA = await h.AddStayOwnerAsync("Host A");
        var hostB = await h.AddStayOwnerAsync("Host B");
        var hostC = await h.AddStayOwnerAsync("Host C");
        var stayA = await h.AddStayAsync(hostA);
        var stayB = await h.AddStayAsync(hostB);
        var stayC = await h.AddStayAsync(hostC);
        var trip = await PlannedTripAsync(h, seeker, Today.AddDays(-2));
        await EnquiryAsync(h, trip, seeker, hostA, stayA, TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-3));
        await EnquiryAsync(h, trip, seeker, hostB, stayB, TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-2));
        await EnquiryAsync(h, trip, seeker, hostC, stayC, TripEnquiryStatuses.CancelledByTraveller, Today.AddDays(-2));

        await h.CompleteAsync(trip, seeker);
        Assert.Equal((0, 1), await CountersAsync(hostA));
        Assert.Equal((0, 1), await CountersAsync(hostB));
        Assert.Equal((0, 0), await CountersAsync(hostC));

        await using (var s = h.NewScope())
            await s.Db.StayListings.Where(l => l.Id == stayA).ExecuteUpdateAsync(u => u.SetProperty(l => l.IsDeleted, true));
        Assert.Equal((0, 1), await CountersAsync(hostA));
    }

    // ---------- JT-B-CNT-3: Taxi Driver ----------

    [IntegrationFact]
    public async Task Driver_counts_a_started_ride_once_and_never_an_unstarted_or_cancelled_one()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var taxi = await h.AddTaxiAsync(driver);
        var trip = await PlannedTripAsync(h, seeker, Today.AddDays(1));

        var ridden = await BookingAsync(h, trip, seeker, driver, taxi, BookingStates.Completed, started: true);
        await Task.WhenAll(CountTaxiAsync(ridden), CountTaxiAsync(ridden));
        await CountTaxiAsync(ridden);
        Assert.Equal((0, 1), await CountersAsync(driver));

        var autoFromBooked = await BookingAsync(h, trip, seeker, driver, taxi, BookingStates.Completed, started: false);
        var cancelled = await BookingAsync(h, trip, seeker, driver, taxi, BookingStates.Cancelled, started: true);
        await CountTaxiAsync(autoFromBooked);
        await CountTaxiAsync(cancelled);
        Assert.Equal((0, 1), await CountersAsync(driver));

        await using (var s = h.NewScope())
            await s.Db.TaxiListings.Where(l => l.Id == taxi).ExecuteUpdateAsync(u => u.SetProperty(l => l.IsDeleted, true));
        Assert.Equal((0, 1), await CountersAsync(driver));
        await CleanupBookingsAsync(ridden, autoFromBooked, cancelled);
    }
}
