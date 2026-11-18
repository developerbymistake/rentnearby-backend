using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class TaxiRequestStoreIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private sealed class NullSignals : IOutboxSignal, IDueActionsSignal, IMatcherSignal, ITaxiRequestEvents
    {
        void IOutboxSignal.Wake() { }
        void IDueActionsSignal.Wake() { }
        public void MarkDirty(long cityKey) { }
        public void MarkDriverFreed(Guid driverListingId) { }
        public Task RequestChangedAsync(Guid requestId, Guid tripId, Guid travellerUserId, long version, string state) => Task.CompletedTask;
    }

    private sealed class CountingPushWriter : ITaxiPushWriter
    {
        public int Count { get; private set; }
        public Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default) { Count++; return Task.CompletedTask; }
        public Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default) { Count++; return Task.CompletedTask; }
        public Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default) { Count++; return Task.CompletedTask; }
    }

    private static TaxiRequestStore Store(ITaxiPushWriter? writer = null)
    {
        var s = new NullSignals();
        return new TaxiRequestStore(writer ?? new CountingPushWriter(), new DueActionScheduler(), s, s, s, s, NullLogger<TaxiRequestStore>.Instance);
    }

    private sealed record Seed(Guid TripId, Guid RequestId, Guid TravellerId, List<(Guid Listing, Guid User)> Drivers);

    private static async Task<Seed> SeedAsync(JourneyHarness h, string status, int drivers)
    {
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 3)));
        var list = new List<(Guid, Guid)>();
        for (var i = 0; i < drivers; i++)
        {
            var user = await h.AddTaxiDriverAsync($"Driver{i}");
            list.Add((await h.AddTaxiAsync(user), user));
        }
        var requestId = Guid.NewGuid();
        await using var db = h.NewScope();
        db.Db.Set<TaxiRequest>().Add(new TaxiRequest
        {
            Id = requestId, TripId = tripId, TravellerUserId = seeker, Status = status, WantSightseeing = true,
            DispatchCityKey = 210000001, MatchToCityKey = 210000001, Seats = 4, Travellers = 2,
            DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 20),
        });
        await db.Db.SaveChangesAsync();
        return new Seed(tripId, requestId, seeker, list);
    }

    private static async Task<Guid> AddOfferAsync(JourneyHarness h, Guid requestId, (Guid Listing, Guid User) driver, string status, int seq)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequestOffer>().Add(new TaxiRequestOffer
        {
            Id = id, RequestId = requestId, DriverListingId = driver.Listing, DriverUserId = driver.User, Status = status, SeqNo = seq,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });
        s.Db.Set<DueAction>().Add(new DueAction
        {
            Id = Guid.NewGuid(), Kind = DueActionKinds.OfferExpire, SubjectId = id, RequestId = requestId, DueAt = DateTime.UtcNow.AddMinutes(5),
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    [IntegrationFact]
    public async Task End_request_closes_offers_cancels_booking_and_timers()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await SeedAsync(h, RequestStates.Booked, 4);
        await AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1);
        await AddOfferAsync(h, seed.RequestId, seed.Drivers[1], OfferStates.Accepted, 2);
        await AddOfferAsync(h, seed.RequestId, seed.Drivers[2], OfferStates.Selected, 3);
        var confirmed = await AddOfferAsync(h, seed.RequestId, seed.Drivers[3], OfferStates.Confirmed, 4);
        var bookingId = Guid.NewGuid();
        await using (var s = h.NewScope())
        {
            s.Db.Set<TaxiBooking>().Add(new TaxiBooking
            {
                Id = bookingId, RequestId = seed.RequestId, OfferId = confirmed, TripId = seed.TripId, DriverListingId = seed.Drivers[3].Listing,
                DriverUserId = seed.Drivers[3].User, TravellerUserId = seed.TravellerId, DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 20),
                BookedAt = DateTime.UtcNow,
            });
            s.Db.Set<DueAction>().Add(new DueAction
            {
                Id = Guid.NewGuid(), Kind = DueActionKinds.BookingStart, SubjectId = bookingId, RequestId = null, DueAt = DateTime.UtcNow.AddDays(10),
            });
            await s.Db.SaveChangesAsync();
        }

        var writer = new CountingPushWriter();
        TaxiEndResult? result;
        await using (var s = h.NewScope())
        {
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            result = await Store(writer).EndRequestAsync(s.Db, seed.RequestId, RequestStates.Cancelled, RequestEndReasons.TravellerCancelled,
                ReasonChips.ChangedPlan, BookingCancelledBy.Traveller);
            await tx.CommitAsync();
        }

        Assert.NotNull(result);
        Assert.Equal(3, result.ClosedOffers.Count);
        Assert.Equal(bookingId, result.Booking?.BookingId);
        Assert.Equal(4, writer.Count);
        await using var read = CreateContext();
        var request = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.Equal(RequestStates.Cancelled, request.Status);
        Assert.NotNull(request.ClosedAt);
        Assert.Equal(2, request.Version);
        var offers = await read.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.RequestId == seed.RequestId).ToListAsync();
        Assert.DoesNotContain(offers, o => OfferStates.Live.Contains(o.Status));
        Assert.Equal(OfferStates.Confirmed, offers.Single(o => o.Id == confirmed).Status);
        Assert.Equal(BookingStates.Cancelled, (await read.Set<TaxiBooking>().AsNoTracking().SingleAsync(b => b.Id == bookingId)).Status);
        var subjects = offers.Select(o => o.Id).Append(bookingId).ToList();
        Assert.False(await read.Set<DueAction>().AnyAsync(d => d.State == DueActionStates.Pending && (d.RequestId == seed.RequestId || subjects.Contains(d.SubjectId))));
    }

    [IntegrationFact]
    public async Task Reopen_picks_collecting_or_open_by_live_accepts()
    {
        await using var h = new JourneyHarness(Db);
        var withAccept = await SeedAsync(h, RequestStates.Selected, 2);
        await AddOfferAsync(h, withAccept.RequestId, withAccept.Drivers[0], OfferStates.Accepted, 1);
        await AddOfferAsync(h, withAccept.RequestId, withAccept.Drivers[1], OfferStates.Missed, 2);
        var without = await SeedAsync(h, RequestStates.Booked, 1);

        await using var s = h.NewScope();
        await using var tx = await s.Db.Database.BeginTransactionAsync();
        var a = await Store().ReopenRequestAsync(s.Db, withAccept.RequestId, 0);
        var b = await Store().ReopenRequestAsync(s.Db, without.RequestId, 3);
        await tx.CommitAsync();

        Assert.Equal(RequestStates.Collecting, a?.ToState);
        Assert.Equal(RequestStates.Open, b?.ToState);
        await using var read = CreateContext();
        var reopened = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == without.RequestId);
        Assert.Equal(TaxiDispatchRules.DefaultAcceptTarget + 3, reopened.AcceptTarget);
        Assert.Null(reopened.SelectedOfferId);
    }

    private async Task<long> VersionAsync(Guid requestId)
    {
        await using var read = CreateContext();
        return (await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == requestId)).Version;
    }

    [IntegrationFact]
    public async Task Offer_booking_and_close_moves_raise_the_request_version_by_one()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await SeedAsync(h, RequestStates.Booked, 3);
        var ringing = await AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1);
        await AddOfferAsync(h, seed.RequestId, seed.Drivers[1], OfferStates.Accepted, 2);
        var confirmed = await AddOfferAsync(h, seed.RequestId, seed.Drivers[2], OfferStates.Confirmed, 3);
        var bookingId = Guid.NewGuid();
        await using (var s = h.NewScope())
        {
            s.Db.Set<TaxiBooking>().Add(new TaxiBooking
            {
                Id = bookingId, RequestId = seed.RequestId, OfferId = confirmed, TripId = seed.TripId, DriverListingId = seed.Drivers[2].Listing,
                DriverUserId = seed.Drivers[2].User, TravellerUserId = seed.TravellerId, DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 20),
                BookedAt = DateTime.UtcNow,
            });
            await s.Db.SaveChangesAsync();
        }
        var store = Store();

        async Task<long> Step(Func<ApplicationDbContext, Task> action)
        {
            var before = await VersionAsync(seed.RequestId);
            await using var s = h.NewScope();
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            await action(s.Db);
            await tx.CommitAsync();
            return await VersionAsync(seed.RequestId) - before;
        }

        Assert.Equal(1, await Step(db => store.TryMoveOfferAsync(db, ringing, [OfferStates.Ringing], OfferStates.Missed)));
        Assert.Equal(1, await Step(db => store.TryMoveBookingAsync(db, bookingId, [BookingStates.Booked], BookingStates.InProgress)));
        Assert.Equal(1, await Step(db => store.CloseOffersAsync(db, seed.RequestId, [OfferStates.Accepted], OfferStates.Closed, OfferEndReasons.RequestCancelled)));
        Assert.Equal(0, await Step(db => store.CloseOffersAsync(db, seed.RequestId, [OfferStates.Accepted], OfferStates.Closed, OfferEndReasons.RequestCancelled)));
        Assert.Equal(0, await Step(db => store.TryMoveOfferAsync(db, ringing, [OfferStates.Ringing], OfferStates.Missed)));
    }

    [IntegrationFact]
    public async Task A_running_trip_is_not_cancelled_by_the_traveller()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await SeedAsync(h, RequestStates.InProgress, 0);

        await using var s = h.NewScope();
        await using var tx = await s.Db.Database.BeginTransactionAsync();
        var store = Store();
        Assert.Null(await store.EndRequestAsync(s.Db, seed.RequestId, RequestStates.Cancelled, RequestEndReasons.TravellerCancelled));
        Assert.NotNull(await store.EndRequestAsync(s.Db, seed.RequestId, RequestStates.Cancelled, RequestEndReasons.DriverLeftTrip,
            cancelledBy: BookingCancelledBy.Driver));
        await tx.CommitAsync();

        await using var read = CreateContext();
        Assert.Equal(RequestStates.Cancelled, (await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId)).Status);
    }

    [IntegrationFact]
    public async Task Move_from_a_wrong_state_changes_nothing()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await SeedAsync(h, RequestStates.Expired, 1);
        var offer = await AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Declined, 1);

        await using var s = h.NewScope();
        await using var tx = await s.Db.Database.BeginTransactionAsync();
        var store = Store();
        Assert.Null(await store.TryMoveRequestAsync(s.Db, seed.RequestId, [RequestStates.Open], RequestStates.Collecting));
        Assert.Null(await store.TryMoveOfferAsync(s.Db, offer, [OfferStates.Ringing], OfferStates.Accepted));
        Assert.Null(await store.EndRequestAsync(s.Db, seed.RequestId, RequestStates.Cancelled, RequestEndReasons.TravellerCancelled));
        Assert.Null(await store.ReopenRequestAsync(s.Db, seed.RequestId, 3));
        await tx.CommitAsync();

        await using var read = CreateContext();
        var request = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.Equal(RequestStates.Expired, request.Status);
        Assert.Equal(1, request.Version);
        Assert.Equal(OfferStates.Declined, (await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == offer)).Status);
    }
}
