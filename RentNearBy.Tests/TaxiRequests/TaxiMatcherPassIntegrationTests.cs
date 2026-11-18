using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class TaxiMatcherPassIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

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
        public int Rings;
        public Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default) { Interlocked.Increment(ref Rings); return Task.CompletedTask; }
        public Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class OnFeature : ITripExecutionFeatureReader
    {
        public Task<bool> IsEnabledAsync() => Task.FromResult(true);
        public void Invalidate() { }
    }

    private sealed class Ctx(TaxiMatcherPassIntegrationTests test, JourneyHarness h) : IAsyncDisposable
    {
        public JourneyHarness H { get; } = h;
        public List<long> Cities { get; } = [];
        public List<Guid> Requests { get; } = [];
        public List<Guid> Districts { get; } = [];

        public long NewCity()
        {
            var key = 900_000_000L + Random.Shared.Next(1, 90_000_000);
            Cities.Add(key);
            return key;
        }

        public async ValueTask DisposeAsync()
        {
            await using (var db = test.CreateContext())
            {
                await db.Set<DueAction>().Where(d => d.RequestId != null && Requests.Contains(d.RequestId.Value)).ExecuteDeleteAsync();
                await db.Set<DriverDispatchState>().Where(d => Cities.Contains(d.CityKey)).ExecuteDeleteAsync();
                await db.Set<TaxiDispatchCity>().Where(d => Cities.Contains(d.CityKey)).ExecuteDeleteAsync();
            }
            await H.DisposeAsync();
            await using var after = test.CreateContext();
            await after.Districts.Where(d => Districts.Contains(d.Id)).ExecuteDeleteAsync();
        }
    }

    private Ctx NewCtx() => new(this, new JourneyHarness(Db));

    private static (TaxiMatcherPass Pass, CountingPushWriter Writer) Pass(ApplicationDbContext db, MatcherSignal signal)
    {
        var writer = new CountingPushWriter();
        var scheduler = new DueActionScheduler();
        var s = new NullSignals();
        var store = new TaxiRequestStore(writer, scheduler, s, s, s, s, NullLogger<TaxiRequestStore>.Instance);
        return (new TaxiMatcherPass(new PostgresMatcherPassUnitFactory(db, store, writer, scheduler), new OnFeature(), signal), writer);
    }

    private async Task<MatcherPassResult> RunAsync(long city, MatcherSignal? signal = null)
    {
        await using var db = CreateContext();
        return await Pass(db, signal ?? new MatcherSignal()).Pass.RunAsync(city, Ct);
    }

    private async Task<Guid> DriverAsync(Ctx c, long toCity, Action<TaxiListing>? tweak = null)
    {
        var user = await c.H.AddTaxiDriverAsync();
        var listing = await c.H.AddTaxiAsync(user);
        await using var db = CreateContext();
        var l = await db.TaxiListings.SingleAsync(x => x.Id == listing);
        l.Seats = 4;
        l.RingReadyAt = DateTime.UtcNow;
        l.RingNotificationsOn = true;
        tweak?.Invoke(l);
        db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = user, Token = $"t-{Guid.NewGuid():N}", IsValid = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.TaxiRoutes.Add(new TaxiRoute
        {
            Id = Guid.NewGuid(), TaxiListingId = listing, RouteIndex = 0, FromName = "From", ToName = "To",
            FromLatitude = 29.2m, FromLongitude = 79.5m, ToLatitude = 29.39m, ToLongitude = 79.45m,
            FromCityKey = toCity, ToCityKey = toCity, Fare = 1500, TripType = "OneWay", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return listing;
    }

    private async Task<Guid> RequestAsync(Ctx c, long dispatchCity, long toCity, DateTime? createdAt = null)
    {
        var seeker = await c.H.AddSeekerAsync();
        var tripId = await c.H.AddTripAsync(seeker, c.H.BuildPlan(("Nainital", 210000001, 3)));
        var id = Guid.NewGuid();
        await using var db = CreateContext();
        db.Set<TaxiRequest>().Add(new TaxiRequest
        {
            Id = id, TripId = tripId, TravellerUserId = seeker, Status = RequestStates.Open, WantSightseeing = true,
            DispatchCityKey = dispatchCity, MatchToCityKey = toCity, Seats = 4, Travellers = 2,
            DateFrom = c.H.Start, DateTo = c.H.Start.AddDays(2),
            CreatedAt = createdAt ?? DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        c.Requests.Add(id);
        return id;
    }

    private async Task<List<TaxiRequestOffer>> OffersAsync(IEnumerable<Guid> requestIds)
    {
        var ids = requestIds.ToList();
        await using var db = CreateContext();
        return await db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => ids.Contains(o.RequestId)).ToListAsync();
    }

    private async Task<TaxiRequest> ReadRequestAsync(Guid id)
    {
        await using var db = CreateContext();
        return await db.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [IntegrationFact]
    public async Task Two_parallel_passes_never_ring_one_driver_twice()
    {
        await using var c = NewCtx();
        var city = c.NewCity();
        var drivers = new[] { await DriverAsync(c, city), await DriverAsync(c, city) };
        for (var i = 0; i < 3; i++) await RequestAsync(c, city, city, DateTime.UtcNow.AddSeconds(i));

        var signal = new MatcherSignal();
        var results = await Task.WhenAll(RunAsync(city, signal), RunAsync(city, signal));

        var offers = await OffersAsync(c.Requests);
        Assert.Equal(2, offers.Count);
        Assert.All(drivers, d => Assert.Single(offers, o => o.DriverListingId == d && o.Status == OfferStates.Ringing));
        if (results.Any(r => r.Outcome == MatcherPassOutcome.LockBusy)) Assert.True(signal.IsDirty(city));
        await using var db = CreateContext();
        Assert.Equal(2, await db.Set<DueAction>().CountAsync(d => d.Kind == DueActionKinds.OfferExpire && c.Requests.Contains(d.RequestId!.Value)));
    }

    [IntegrationFact]
    public async Task Two_cities_sharing_a_driver_ring_him_once()
    {
        await using var c = NewCtx();
        var cityA = c.NewCity();
        var cityB = c.NewCity();
        var to = c.NewCity();
        var driver = await DriverAsync(c, to);
        var a = await RequestAsync(c, cityA, to);
        var b = await RequestAsync(c, cityB, to);

        await Task.WhenAll(RunAsync(cityA), RunAsync(cityB));

        var offers = await OffersAsync([a, b]);
        Assert.Single(offers, o => o.DriverListingId == driver && o.Status == OfferStates.Ringing);
        var unserved = offers.Single().RequestId == a ? b : a;
        await RunAsync(unserved == a ? cityA : cityB);
        var waiting = await ReadRequestAsync(unserved);
        Assert.NotNull(waiting.QueueWaitStartedAt);
        Assert.Single(await OffersAsync([a, b]));
    }

    // A ringing offer inserted (uncommitted) by another transaction for the same driver blocks the ON CONFLICT insert.
    [IntegrationFact]
    public async Task Held_offer_insert_ends_the_pass_as_lock_busy_without_a_long_wait()
    {
        await using var c = NewCtx();
        var city = c.NewCity();
        var driver = await DriverAsync(c, city);
        var blocker = await RequestAsync(c, c.NewCity(), city);
        var id = await RequestAsync(c, city, city);

        await using var other = CreateContext();
        await using var otherTx = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlRawAsync($$"""
            INSERT INTO "TaxiRequestOffers" ("Id", "RequestId", "DriverListingId", "DriverUserId", "Status", "SeqNo", "OfferedAt", "ExpiresAt", "PushLate")
            SELECT gen_random_uuid(), {0}, l."Id", l."UserId", '{{OfferStates.Ringing}}', 1, now(), now() + interval '60 seconds', false
            FROM "TaxiListings" l WHERE l."Id" = {1}
            """, blocker, driver);

        var signal = new MatcherSignal();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await RunAsync(city, signal);
        watch.Stop();

        Assert.Equal(MatcherPassOutcome.LockBusy, result.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), $"pass waited {watch.Elapsed}");
        Assert.True(signal.IsDirty(city));
        Assert.Empty(await OffersAsync([id]));

        await otherTx.RollbackAsync();
        await RunAsync(city);
        Assert.Single(await OffersAsync([id]), o => o.DriverListingId == driver && o.Status == OfferStates.Ringing);
    }

    // Another transaction holds the request's pending timer row, which the request end must cancel.
    [IntegrationFact]
    public async Task Held_due_action_row_ends_the_pass_as_lock_busy_without_a_long_wait()
    {
        await using var c = NewCtx();
        var city = c.NewCity();
        var id = await RequestAsync(c, city, city);
        await DriverAsync(c, c.NewCity());
        await using (var seed = CreateContext())
        {
            await new DueActionScheduler().AddAsync(seed, DueActionKinds.RequestWaitExpire, id, id, 1, TimeSpan.FromMinutes(10));
        }

        await using var other = CreateContext();
        await using var otherTx = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlRawAsync("""SELECT 1 FROM "DueActions" WHERE "RequestId" = {0} FOR UPDATE""", id);

        var signal = new MatcherSignal();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await RunAsync(city, signal);
        watch.Stop();

        Assert.Equal(MatcherPassOutcome.LockBusy, result.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), $"pass waited {watch.Elapsed}");
        Assert.True(signal.IsDirty(city));
        Assert.Equal(RequestStates.Open, (await ReadRequestAsync(id)).Status);

        await otherTx.RollbackAsync();
        await RunAsync(city);
        Assert.Equal(RequestStates.Expired, (await ReadRequestAsync(id)).Status);
    }

    [IntegrationFact]
    public async Task Exhausted_request_with_no_live_offer_expires_as_all_drivers_busy()
    {
        await using var c = NewCtx();
        var city = c.NewCity();
        var id = await RequestAsync(c, city, city);
        await DriverAsync(c, c.NewCity());

        var result = await RunAsync(city);

        Assert.Equal(MatcherPassOutcome.Done, result.Outcome);
        var request = await ReadRequestAsync(id);
        Assert.Equal(RequestStates.Expired, request.Status);
        Assert.Equal(RequestEndReasons.AllDriversBusy, request.EndReason);
        Assert.Empty(await OffersAsync([id]));
    }

    [IntegrationFact]
    public async Task Busy_only_pool_starts_the_ten_minute_wait()
    {
        await using var c = NewCtx();
        var city = c.NewCity();
        var other = c.NewCity();
        var driver = await DriverAsync(c, city);
        var first = await RequestAsync(c, other, city);
        await RunAsync(other);
        Assert.Single(await OffersAsync([first]), o => o.DriverListingId == driver);

        var id = await RequestAsync(c, city, city);
        await RunAsync(city);

        var request = await ReadRequestAsync(id);
        Assert.Equal(RequestStates.Open, request.Status);
        Assert.NotNull(request.QueueWaitStartedAt);
        Assert.Equal(1, request.WaitRound);
        Assert.Empty(await OffersAsync([id]));
        await using var db = CreateContext();
        var due = await db.Set<DueAction>().AsNoTracking().SingleAsync(d => d.Kind == DueActionKinds.RequestWaitExpire && d.SubjectId == id);
        Assert.Equal(1, due.RoundNo);
        Assert.Equal(request.QueueWaitStartedAt!.Value.AddSeconds(TaxiDispatchRules.QueueWaitSeconds), due.DueAt, TimeSpan.FromSeconds(1));

        await RunAsync(city);
        Assert.Equal(1, (await ReadRequestAsync(id)).WaitRound);
    }

    [IntegrationFact]
    public async Task Driver_in_an_inactive_district_is_not_rung()
    {
        await using var c = NewCtx();
        var city = c.NewCity();
        var districtId = Guid.NewGuid();
        await using (var db = CreateContext())
        {
            db.Districts.Add(new District { Id = districtId, Name = $"Closed {districtId:N}", StateName = "Test", IsActive = false, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        c.Districts.Add(districtId);
        var blocked = await DriverAsync(c, city, l => l.DistrictId = districtId);
        var ready = await DriverAsync(c, city);
        var id = await RequestAsync(c, city, city);

        await RunAsync(city);

        var offers = await OffersAsync([id]);
        Assert.DoesNotContain(offers, o => o.DriverListingId == blocked);
        Assert.Contains(offers, o => o.DriverListingId == ready);
    }

    [IntegrationFact]
    public async Task Driver_without_ring_readiness_is_not_rung()
    {
        await using var c = NewCtx();
        var city = c.NewCity();
        var notReady = await DriverAsync(c, city, l => l.RingReadyAt = null);
        var notificationsOff = await DriverAsync(c, city, l => l.RingNotificationsOn = false);
        var ready = await DriverAsync(c, city);
        var id = await RequestAsync(c, city, city);

        await RunAsync(city);

        var offers = await OffersAsync([id]);
        Assert.DoesNotContain(offers, o => o.DriverListingId == notReady || o.DriverListingId == notificationsOff);
        Assert.Contains(offers, o => o.DriverListingId == ready);
    }

    [IntegrationFact]
    public async Task Driver_at_the_missed_rings_limit_is_not_rung()
    {
        await using var c = NewCtx();
        var city = c.NewCity();
        var limited = await DriverAsync(c, city, l => l.RingMissStreak = TaxiDispatchRules.RingMissLimit);
        var belowLimit = await DriverAsync(c, city, l => l.RingMissStreak = TaxiDispatchRules.RingMissLimit - 1);
        var id = await RequestAsync(c, city, city);

        await RunAsync(city);

        var offers = await OffersAsync([id]);
        Assert.DoesNotContain(offers, o => o.DriverListingId == limited);
        Assert.Contains(offers, o => o.DriverListingId == belowLimit);
    }
}
