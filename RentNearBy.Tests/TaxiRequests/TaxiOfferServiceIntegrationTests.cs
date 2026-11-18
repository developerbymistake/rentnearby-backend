using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

internal static class TaxiOfferKit
{
    public sealed class NullSignals : IOutboxSignal, IDueActionsSignal, IMatcherSignal, ITaxiRequestEvents
    {
        void IOutboxSignal.Wake() { }
        void IDueActionsSignal.Wake() { }
        public void MarkDirty(long cityKey) { }
        public void MarkDriverFreed(Guid driverListingId) { }
        public Task RequestChangedAsync(Guid requestId, Guid tripId, Guid travellerUserId, long version, string state) => Task.CompletedTask;
    }

    public sealed class RecordingPushes : ITaxiPushWriter
    {
        public List<string> Sent { get; } = [];
        public Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default) { Sent.Add("ring"); return Task.CompletedTask; }
        public Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default) { Sent.Add("ringend"); return Task.CompletedTask; }
        public Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default) { Sent.Add(notice.Kind.ToString()); return Task.CompletedTask; }
    }

    public sealed class NoCities : ICityResolver
    {
        public double BoxKm => 25;
        public Task<CityPin> ResolveAsync(double lat, double lng) => throw new NotSupportedException();
        public Task<CityPin> FindAsync(long cityKey) => Task.FromResult(new CityPin(true, null));
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey) => throw new NotSupportedException();
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    public sealed record Kit(TaxiRequestStore Store, TaxiOfferService Offers, TaxiBookingService Bookings, RecordingPushes Pushes, TaxiPickupPlaces Places);

    public static Kit For(ApplicationDbContext db, RecordingPushes? pushes = null)
    {
        var p = pushes ?? new RecordingPushes();
        var s = new NullSignals();
        var scheduler = new DueActionScheduler();
        var store = new TaxiRequestStore(p, scheduler, s, s, s, s, NullLogger<TaxiRequestStore>.Instance);
        var offers = new TaxiOfferService(db, store, p, scheduler, NullLogger<TaxiOfferService>.Instance);
        var bookings = new TaxiBookingService(db, store, offers, p, scheduler, NullLogger<TaxiBookingService>.Instance);
        return new Kit(store, offers, bookings, p, new TaxiPickupPlaces(db, new NoCities()));
    }

    public sealed record Seed(Guid TripId, Guid RequestId, Guid TravellerId, List<(Guid Listing, Guid User)> Drivers, DateOnly From, DateOnly To);

    public static async Task<Seed> SeedAsync(JourneyHarness h, string status, int drivers, short accepted = 0, short target = 3, int startOffsetDays = 10, int days = 3)
    {
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 3)));
        var list = new List<(Guid, Guid)>();
        for (var i = 0; i < drivers; i++)
        {
            var user = await h.AddTaxiDriverAsync($"Driver{i}");
            list.Add((await h.AddTaxiAsync(user), user));
        }
        var from = TripExecutionRules.TodayIst(DateTime.UtcNow).AddDays(startOffsetDays);
        var to = from.AddDays(days - 1);
        var requestId = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequest>().Add(new TaxiRequest
        {
            Id = requestId, TripId = tripId, TravellerUserId = seeker, Status = status, WantSightseeing = true,
            DispatchCityKey = 210000001, MatchToCityKey = 210000001, Seats = 4, Travellers = 2,
            DateFrom = from, DateTo = to, AcceptedCount = accepted, AcceptTarget = target,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        return new Seed(tripId, requestId, seeker, list, from, to);
    }

    public static async Task<Guid> AddOfferAsync(JourneyHarness h, Guid requestId, (Guid Listing, Guid User) driver, string status, int seq,
        DateTime? expiresAt = null, DateTime? confirmBy = null, bool select = false)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequestOffer>().Add(new TaxiRequestOffer
        {
            Id = id, RequestId = requestId, DriverListingId = driver.Listing, DriverUserId = driver.User, Status = status, SeqNo = seq,
            OfferedAt = DateTime.UtcNow, ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(5), ConfirmBy = confirmBy,
        });
        if (status == OfferStates.Ringing)
            s.Db.Set<DueAction>().Add(new DueAction { Id = Guid.NewGuid(), Kind = DueActionKinds.OfferExpire, SubjectId = id, RequestId = requestId, DueAt = expiresAt ?? DateTime.UtcNow.AddMinutes(5) });
        await s.Db.SaveChangesAsync();
        if (select)
            await s.Db.Set<TaxiRequest>().Where(r => r.Id == requestId).ExecuteUpdateAsync(u => u.SetProperty(r => r.SelectedOfferId, id));
        return id;
    }

    public static async Task<Guid> AddBookingAsync(JourneyHarness h, Seed seed, Guid offerId, (Guid Listing, Guid User) driver, string status,
        DateOnly from, DateOnly to, Guid? requestId = null)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiBooking>().Add(new TaxiBooking
        {
            Id = id, RequestId = requestId ?? seed.RequestId, OfferId = offerId, TripId = seed.TripId, DriverListingId = driver.Listing,
            DriverUserId = driver.User, TravellerUserId = seed.TravellerId, DateFrom = from, DateTo = to, Status = status, BookedAt = DateTime.UtcNow,
            DriverName = "Driver",
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    public static async Task<Guid> AddLegacyLocalTaxiAsync(JourneyHarness h, Guid tripId, Guid travellerId, (Guid Listing, Guid User) driver,
        string status, DateOnly checkIn, DateOnly lastDay)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.TripEnquiries.Add(new TripEnquiry
        {
            Id = id, TripId = tripId, TravellerUserId = travellerId, OwnerUserId = driver.User, Kind = TripEnquiryKinds.Taxi,
            ListingId = driver.Listing, ListingName = "Taxi", CheckIn = checkIn, CheckOut = lastDay.AddDays(1), CityLeg = 1,
            Status = status, ExpiresAt = DateTime.UtcNow.AddDays(30), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            BookedByTravellerAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        return id;
    }
}

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class TaxiOfferServiceIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task Accept_after_expiry_is_missed()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 1);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1, expiresAt: DateTime.UtcNow.AddSeconds(-5));

        await using var s = h.NewScope();
        var result = await TaxiOfferKit.For(s.Db).Offers.AcceptAsync(offer, seed.Drivers[0].User);

        Assert.Equal(TaxiRequestErrorTypes.OfferMissed, result.Error);
        await using var read = CreateContext();
        var request = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.Equal(0, request.AcceptedCount);
        Assert.Equal(RequestStates.Open, request.Status);
    }

    [IntegrationFact]
    public async Task Fourth_accept_is_missed_and_closes_other_rings()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Collecting, 3, accepted: 2);
        var a = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1);
        var b = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[1], OfferStates.Ringing, 2);

        var pushes = new TaxiOfferKit.RecordingPushes();
        await using (var s = h.NewScope())
            Assert.True((await TaxiOfferKit.For(s.Db, pushes).Offers.AcceptAsync(a, seed.Drivers[0].User)).Ok);
        Assert.Contains("ringend", pushes.Sent);
        Assert.Contains(nameof(TaxiNoticeKind.DriverAccepted), pushes.Sent);

        await using (var s = h.NewScope())
            Assert.Equal(TaxiRequestErrorTypes.OfferMissed, (await TaxiOfferKit.For(s.Db).Offers.AcceptAsync(b, seed.Drivers[1].User)).Error);

        await using var read = CreateContext();
        var request = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.Equal(3, request.AcceptedCount);
        var offers = await read.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.RequestId == seed.RequestId).ToDictionaryAsync(o => o.Id);
        Assert.Equal(OfferStates.Accepted, offers[a].Status);
        Assert.Equal(OfferStates.Closed, offers[b].Status);
        Assert.Equal(OfferEndReasons.Filled, offers[b].EndReason);
    }

    [IntegrationFact]
    public async Task Accept_on_a_full_request_misses_the_ring()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Collecting, 1, accepted: 3);
        var c = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1);
        await using (var s = h.NewScope())
            Assert.Equal(TaxiRequestErrorTypes.OfferMissed, (await TaxiOfferKit.For(s.Db).Offers.AcceptAsync(c, seed.Drivers[0].User)).Error);
        await using var read = CreateContext();
        var offer = await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == c);
        Assert.Equal((OfferStates.Missed, OfferEndReasons.Filled), (offer.Status, offer.EndReason));
        Assert.Equal(3, (await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId)).AcceptedCount);
    }

    [IntegrationFact]
    public async Task Accept_twice_is_idempotent()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 1);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1);
        for (var i = 0; i < 2; i++)
        {
            await using var s = h.NewScope();
            Assert.True((await TaxiOfferKit.For(s.Db).Offers.AcceptAsync(offer, seed.Drivers[0].User)).Ok);
        }
        await using var read = CreateContext();
        var request = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.Equal((RequestStates.Collecting, (short)1), (request.Status, request.AcceptedCount));
    }

    [IntegrationFact]
    public async Task Confirm_after_the_window_fails()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Selected, 1, accepted: 1);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Selected, 1,
            confirmBy: DateTime.UtcNow.AddSeconds(-1), select: true);
        await using var s = h.NewScope();
        var result = await TaxiOfferKit.For(s.Db).Bookings.ConfirmAsync(offer, seed.Drivers[0].User);
        Assert.Equal(TaxiRequestErrorTypes.ConfirmWindowPassed, result.Error);
        await using var read = CreateContext();
        Assert.False(await read.Set<TaxiBooking>().AnyAsync(b => b.RequestId == seed.RequestId));
    }

    [IntegrationFact]
    public async Task Confirm_books_and_closes_the_other_offers()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Selected, 3, accepted: 2);
        var chosen = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Selected, 1, confirmBy: DateTime.UtcNow.AddMinutes(10), select: true);
        var other = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[1], OfferStates.Accepted, 2);
        var ringing = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[2], OfferStates.Ringing, 3);

        await using (var s = h.NewScope())
            Assert.True((await TaxiOfferKit.For(s.Db).Bookings.ConfirmAsync(chosen, seed.Drivers[0].User)).Ok);
        await using (var s = h.NewScope())
            Assert.True((await TaxiOfferKit.For(s.Db).Bookings.ConfirmAsync(chosen, seed.Drivers[0].User)).Ok);

        await using var read = CreateContext();
        Assert.Equal(RequestStates.Booked, (await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId)).Status);
        var offers = await read.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.RequestId == seed.RequestId).ToDictionaryAsync(o => o.Id);
        Assert.Equal(OfferStates.Confirmed, offers[chosen].Status);
        Assert.Equal(OfferStates.NotSelected, offers[other].Status);
        Assert.Equal(OfferStates.Closed, offers[ringing].Status);
        var booking = await read.Set<TaxiBooking>().AsNoTracking().SingleAsync(b => b.RequestId == seed.RequestId);
        Assert.Equal((seed.From, seed.To), (booking.DateFrom, booking.DateTo));
        var kinds = await read.Set<DueAction>().AsNoTracking().Where(d => d.SubjectId == booking.Id && d.State == DueActionStates.Pending).Select(d => d.Kind).ToListAsync();
        Assert.Equal([DueActionKinds.BookingAutoComplete, DueActionKinds.BookingStart], kinds.Order().ToList());
    }

    [IntegrationFact]
    public async Task Legacy_confirm_respects_a_new_booking()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Booked, 1);
        var driver = seed.Drivers[0];
        await TaxiOfferKit.AddBookingAsync(h, seed, Guid.NewGuid(), driver, BookingStates.Booked, seed.From, seed.To);
        var otherTraveller = await h.AddSeekerAsync("Other");
        var otherTrip = await h.AddTripAsync(otherTraveller, h.BuildPlan(("Nainital", 210000001, 3)));
        var legacy = await TaxiOfferKit.AddLegacyLocalTaxiAsync(h, otherTrip, otherTraveller, driver, TripEnquiryStatuses.BookedByTraveller, seed.To, seed.To.AddDays(1));

        await using var s = h.NewScope();
        var result = await TripLeadHandlers.Confirm(legacy, JourneyHarness.Principal(driver.User), s.Uow, s.Db, h.RateLimit, h.Notifier, s.Http, NullLoggerFactory.Instance);

        Assert.Equal(409, JourneyHarness.StatusOf(result));
        await using var read = CreateContext();
        Assert.Equal(TripEnquiryStatuses.BookedByTraveller, (await read.TripEnquiries.AsNoTracking().SingleAsync(e => e.Id == legacy)).Status);
    }

    [IntegrationFact]
    public async Task New_confirm_respects_a_legacy_confirmed_row()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Selected, 1, accepted: 1);
        var driver = seed.Drivers[0];
        var otherTraveller = await h.AddSeekerAsync("Other");
        var otherTrip = await h.AddTripAsync(otherTraveller, h.BuildPlan(("Nainital", 210000001, 3)));
        await TaxiOfferKit.AddLegacyLocalTaxiAsync(h, otherTrip, otherTraveller, driver, TripEnquiryStatuses.ConfirmedByBoth, seed.From.AddDays(-2), seed.From);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, driver, OfferStates.Selected, 1, confirmBy: DateTime.UtcNow.AddMinutes(10), select: true);

        await using var s = h.NewScope();
        var result = await TaxiOfferKit.For(s.Db).Bookings.ConfirmAsync(offer, driver.User);

        Assert.Equal(TaxiRequestErrorTypes.DatesAlreadyBooked, result.Error);
        await using var read = CreateContext();
        Assert.Equal(RequestStates.Selected, (await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId)).Status);
        Assert.False(await read.Set<TaxiBooking>().AnyAsync(b => b.RequestId == seed.RequestId));
    }

    [IntegrationFact]
    public async Task Withdraw_before_start_reopens_the_request()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Booked, 1, accepted: 3);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Confirmed, 1);
        var booking = await TaxiOfferKit.AddBookingAsync(h, seed, offer, seed.Drivers[0], BookingStates.Booked, seed.From, seed.To);

        var pushes = new TaxiOfferKit.RecordingPushes();
        await using (var s = h.NewScope())
            Assert.True((await TaxiOfferKit.For(s.Db, pushes).Offers.WithdrawAsync(offer, seed.Drivers[0].User, ReasonChips.VehicleIssue)).Ok);

        Assert.Contains(nameof(TaxiNoticeKind.DriverWithdrew), pushes.Sent);
        await using var read = CreateContext();
        var request = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.Equal((RequestStates.Open, (short)6), (request.Status, request.AcceptTarget));
        Assert.Equal(BookingStates.Cancelled, (await read.Set<TaxiBooking>().AsNoTracking().SingleAsync(b => b.Id == booking)).Status);
        Assert.Equal(OfferStates.Withdrawn, (await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == offer)).Status);
        Assert.True(await read.Set<DueAction>().AnyAsync(d => d.SubjectId == seed.RequestId && d.Kind == DueActionKinds.RequestStaleExpire && d.State == DueActionStates.Pending));
    }

    [IntegrationFact]
    public async Task Withdraw_during_trip_ends_the_request()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.InProgress, 1, accepted: 1, startOffsetDays: 0);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Confirmed, 1);
        var booking = await TaxiOfferKit.AddBookingAsync(h, seed, offer, seed.Drivers[0], BookingStates.InProgress, seed.From, seed.To);

        await using (var s = h.NewScope())
            Assert.True((await TaxiOfferKit.For(s.Db).Offers.WithdrawAsync(offer, seed.Drivers[0].User, ReasonChips.Other)).Ok);

        await using var read = CreateContext();
        var request = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.Equal((RequestStates.Cancelled, RequestEndReasons.DriverLeftTrip), (request.Status, request.EndReason));
        var b = await read.Set<TaxiBooking>().AsNoTracking().SingleAsync(x => x.Id == booking);
        Assert.Equal((BookingStates.Cancelled, BookingCancelledBy.Driver), (b.Status, b.CancelledBy));
    }

    [IntegrationFact]
    public async Task Auto_complete_counts_the_ride_and_asks_for_a_rating()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.InProgress, 1, accepted: 1, startOffsetDays: -3);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Confirmed, 1);
        var booking = await TaxiOfferKit.AddBookingAsync(h, seed, offer, seed.Drivers[0], BookingStates.InProgress, seed.From, seed.To);

        var pushes = new TaxiOfferKit.RecordingPushes();
        await using (var s = h.NewScope())
        {
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            var kit = TaxiOfferKit.For(s.Db, pushes);
            var handler = new BookingAutoCompleteHandler(kit.Store, kit.Bookings);
            var item = new DueActionItem(Guid.NewGuid(), DueActionKinds.BookingAutoComplete, booking, 0, seed.RequestId, DateTime.UtcNow, 0);
            Assert.Equal(DueOutcome.Done, await handler.HandleAsync(s.Db, item, CancellationToken.None));
            await s.Db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        Assert.Equal([nameof(TaxiNoticeKind.RateDriver)], pushes.Sent);
        await using var read = CreateContext();
        Assert.Equal(1, (await read.TaxiListings.AsNoTracking().SingleAsync(t => t.Id == seed.Drivers[0].Listing)).RidesCompleted);
        var b = await read.Set<TaxiBooking>().AsNoTracking().SingleAsync(x => x.Id == booking);
        Assert.Equal((BookingStates.Completed, BookingCompletedBy.Auto), (b.Status, b.CompletedBy));
        Assert.Equal(RequestStates.Completed, (await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId)).Status);
    }

    [IntegrationFact]
    public async Task Second_rating_with_another_value_conflicts()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Completed, 1, accepted: 1, startOffsetDays: -5);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Confirmed, 1);
        await TaxiOfferKit.AddBookingAsync(h, seed, offer, seed.Drivers[0], BookingStates.Completed, seed.From, seed.To);

        async Task<TaxiActionResult> Rate(int stars)
        {
            await using var s = h.NewScope();
            return await TaxiOfferKit.For(s.Db).Bookings.RateAsync(seed.RequestId, seed.TravellerId, stars);
        }

        Assert.True((await Rate(5)).Ok);
        Assert.True((await Rate(5)).Ok);
        Assert.Equal(TaxiRequestErrorTypes.AlreadyRated, (await Rate(4)).Error);

        await using var read = CreateContext();
        var listing = await read.TaxiListings.AsNoTracking().SingleAsync(t => t.Id == seed.Drivers[0].Listing);
        Assert.Equal((5, 1), (listing.RatingSum, listing.RatingCount));
    }

    [IntegrationFact]
    public async Task Rating_before_completion_is_not_allowed()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.InProgress, 1, accepted: 1, startOffsetDays: 0);
        await using var s = h.NewScope();
        Assert.Equal(TaxiRequestErrorTypes.RatingNotAllowed, (await TaxiOfferKit.For(s.Db).Bookings.RateAsync(seed.RequestId, seed.TravellerId, 5)).Error);
    }

    [IntegrationFact]
    public async Task Timer_firing_twice_changes_state_once()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 1);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1, expiresAt: DateTime.UtcNow.AddSeconds(-1));

        var outcomes = new List<DueOutcome>();
        for (var i = 0; i < 2; i++)
        {
            await using var s = h.NewScope();
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            var kit = TaxiOfferKit.For(s.Db);
            var item = new DueActionItem(Guid.NewGuid(), DueActionKinds.OfferExpire, offer, 0, seed.RequestId, DateTime.UtcNow, 0);
            outcomes.Add(await new OfferExpireHandler(kit.Store, kit.Pushes, kit.Places).HandleAsync(s.Db, item, CancellationToken.None));
            await s.Db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        Assert.Equal([DueOutcome.Done, DueOutcome.Skipped], outcomes);
        await using var read = CreateContext();
        Assert.Equal(1, (await read.TaxiListings.AsNoTracking().SingleAsync(t => t.Id == seed.Drivers[0].Listing)).RingMissStreak);
        var o = await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(x => x.Id == offer);
        Assert.Equal((OfferStates.Missed, OfferEndReasons.Expired), (o.Status, o.EndReason));
    }

    [IntegrationFact]
    public async Task Third_unacked_timeout_sends_the_rings_missed_notice_once()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 1);
        await using (var s = h.NewScope())
            await s.Db.TaxiListings.Where(t => t.Id == seed.Drivers[0].Listing).ExecuteUpdateAsync(u => u.SetProperty(t => t.RingMissStreak, (short)2));
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1, expiresAt: DateTime.UtcNow.AddSeconds(-1));

        var pushes = new TaxiOfferKit.RecordingPushes();
        await using (var s = h.NewScope())
        {
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            var kit = TaxiOfferKit.For(s.Db, pushes);
            var item = new DueActionItem(Guid.NewGuid(), DueActionKinds.OfferExpire, offer, 0, seed.RequestId, DateTime.UtcNow, 0);
            Assert.Equal(DueOutcome.Done, await new OfferExpireHandler(kit.Store, kit.Pushes, kit.Places).HandleAsync(s.Db, item, CancellationToken.None));
            await tx.CommitAsync();
        }
        Assert.Equal([nameof(TaxiNoticeKind.RingsMissed)], pushes.Sent);
    }
}
