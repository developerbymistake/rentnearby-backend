using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class TaxiRequestServiceIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private const long Nainital = 210000001, Kathgodam = 210000050;

    private sealed class Signals : IOutboxSignal, IDueActionsSignal, IMatcherSignal, ITaxiRequestEvents
    {
        public List<long> Dirty { get; } = [];
        void IOutboxSignal.Wake() { }
        void IDueActionsSignal.Wake() { }
        public void MarkDirty(long cityKey) => Dirty.Add(cityKey);
        public void MarkDriverFreed(Guid driverListingId) { }
        public Task RequestChangedAsync(Guid requestId, Guid tripId, Guid travellerUserId, long version, string state) => Task.CompletedTask;
    }

    private sealed class Pushes : ITaxiPushWriter
    {
        public List<TaxiNotice> Notices { get; } = [];
        public Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default) => Task.CompletedTask;
        public Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default) { Notices.Add(notice); return Task.CompletedTask; }
    }

    private sealed class Resolver : ICityResolver
    {
        private static readonly Dictionary<long, string> Names = new() { [Nainital] = "Nainital", [Kathgodam] = "Kathgodam" };
        public double BoxKm => 25;
        public Task<CityPin> ResolveAsync(double lat, double lng) => throw new NotSupportedException();
        public Task<CityPin> FindAsync(long cityKey)
            => Task.FromResult(new CityPin(true, Names.TryGetValue(cityKey, out var n) ? new TripCityInfo { CityKey = cityKey, Name = n } : null));
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey) => throw new NotSupportedException();
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    private readonly Signals _signals = new();
    private readonly Pushes _pushes = new();

    private TaxiRequestStore Store()
        => new(_pushes, new DueActionScheduler(), _signals, _signals, _signals, _signals, NullLogger<TaxiRequestStore>.Instance);

    private TaxiRequestService Service(ApplicationDbContext db)
        => new(db, Store(), new TaxiPickupPlaces(db, new Resolver()), new DueActionScheduler(), _pushes, new InMemoryRateLimitService(),
            NullLogger<TaxiRequestService>.Instance);

    private static CreateTaxiRequestDto Body(Guid? clientId = null) => new()
    {
        ClientRequestId = clientId ?? Guid.NewGuid(),
        Pickup = new CreateTaxiPickupDto { On = true, CityKey = Kathgodam },
        Sightseeing = new CreateTaxiSightseeingDto { On = true, Days = [1, 2] },
        Drop = new CreateTaxiDropDto { On = true, SameAsPickup = true },
        Seats = 4,
        DayStats = [new TaxiDayStatDto { Day = 1, Km = 25, Minutes = 300 }],
    };

    // A pool driver (MASTER_PLAN 2.5 static rule) with a route Kathgodam -> Nainital.
    private static async Task<(Guid Listing, Guid User)> ReadyDriverAsync(JourneyHarness h, string name)
    {
        var user = await h.AddTaxiDriverAsync(name);
        var listing = await h.AddTaxiAsync(user);
        await using var s = h.NewScope();
        await s.Db.TaxiListings.Where(l => l.Id == listing).ExecuteUpdateAsync(u => u
            .SetProperty(l => l.Seats, (short)4).SetProperty(l => l.RingReadyAt, DateTime.UtcNow).SetProperty(l => l.RingNotificationsOn, true));
        s.Db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = user, Token = "t-" + Guid.NewGuid(), IsValid = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        s.Db.TaxiRoutes.Add(new TaxiRoute
        {
            Id = Guid.NewGuid(), TaxiListingId = listing, FromName = "Kathgodam", ToName = "Nainital", FromCityKey = Kathgodam, ToCityKey = Nainital,
            FromLatitude = 29.27m, FromLongitude = 79.54m, ToLatitude = 29.39m, ToLongitude = 79.45m, Fare = 1500, TripType = "OneWay",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        return (listing, user);
    }

    private static async Task<(Guid Trip, Guid Seeker)> TripAsync(JourneyHarness h)
    {
        var seeker = await h.AddSeekerAsync();
        return (await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 3))), seeker);
    }

    private async Task<TaxiRequest> CreateAsync(JourneyHarness h, Guid trip, Guid seeker)
    {
        await using var s = h.NewScope();
        var r = await Service(s.Db).CreateAsync(trip, seeker, Body());
        Assert.True(r.IsOk, r.Error?.Type);
        return r.Request!;
    }

    private static async Task<Guid> OfferAsync(JourneyHarness h, Guid requestId, (Guid Listing, Guid User) driver, string status)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequestOffer>().Add(new TaxiRequestOffer
        {
            Id = id, RequestId = requestId, DriverListingId = driver.Listing, DriverUserId = driver.User, Status = status, SeqNo = 1,
            OfferedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });
        s.Db.Set<DueAction>().Add(new DueAction { Id = Guid.NewGuid(), Kind = DueActionKinds.OfferExpire, SubjectId = id, RequestId = requestId, DueAt = DateTime.UtcNow.AddMinutes(5) });
        await s.Db.SaveChangesAsync();
        return id;
    }

    private static async Task SetAsync(JourneyHarness h, Guid requestId, string status, short accepted, short target, DateTime? waitStarted = null)
    {
        await using var s = h.NewScope();
        await s.Db.Set<TaxiRequest>().Where(r => r.Id == requestId).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Status, status).SetProperty(r => r.AcceptTarget, target).SetProperty(r => r.AcceptedCount, accepted)
            .SetProperty(r => r.QueueWaitStartedAt, waitStarted));
    }

    private async Task<TaxiRequest> ReadAsync(Guid requestId)
    {
        await using var read = CreateContext();
        return await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == requestId);
    }

    private async Task<DueOutcome> RunHandlerAsync(JourneyHarness h, IDueActionHandler handler, Guid requestId)
    {
        await using var s = h.NewScope();
        await using var tx = await s.Db.Database.BeginTransactionAsync();
        var outcome = await handler.HandleAsync(s.Db, new DueActionItem(Guid.NewGuid(), handler.Kind, requestId, 0, requestId, DateTime.UtcNow, 0), default);
        await tx.CommitAsync();
        return outcome;
    }

    [IntegrationFact]
    public async Task Second_create_with_same_client_id_returns_the_first()
    {
        await using var h = new JourneyHarness(Db);
        await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var body = Body();

        TaxiRequestResult first, second;
        await using (var s = h.NewScope()) first = await Service(s.Db).CreateAsync(trip, seeker, body);
        await using (var s = h.NewScope()) second = await Service(s.Db).CreateAsync(trip, seeker, body);

        Assert.True(first.Created);
        Assert.Equal(RequestStates.Open, first.Request!.Status);
        Assert.False(second.Created);
        Assert.Equal(first.Request.Id, second.Request!.Id);
        Assert.Contains(Kathgodam, _signals.Dirty);
        Assert.Equal((Kathgodam, Nainital, (long?)Kathgodam), (first.Request.DispatchCityKey, first.Request.MatchToCityKey, first.Request.MatchFromCityKey));
        Assert.Equal("Nainital", TaxiTripSnapshot.FromJson(first.Request.TripSnapshotJson)!.Name);
        await using var read = CreateContext();
        var stale = await read.Set<DueAction>().AsNoTracking().SingleAsync(d => d.SubjectId == first.Request.Id && d.Kind == DueActionKinds.RequestStaleExpire);
        Assert.Equal(DueActionStates.Pending, stale.State);
        Assert.Equal(TaxiDispatchRules.StaleDueAt(first.Request.DateFrom), stale.DueAt.ToUniversalTime());
    }

    [IntegrationFact]
    public async Task Second_create_with_new_id_conflicts()
    {
        await using var h = new JourneyHarness(Db);
        await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var first = await CreateAsync(h, trip, seeker);

        await using var s = h.NewScope();
        var second = await Service(s.Db).CreateAsync(trip, seeker, Body());
        Assert.Equal(TaxiRequestErrorTypes.TaxiRequestAlreadyActive, second.Error!.Type);
        Assert.Equal(first.Id, ((TaxiRequestAlreadyActiveData)second.Error.Data!).RequestId);
    }

    [IntegrationFact]
    public async Task Create_is_refused_while_a_legacy_local_taxi_ask_is_open()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var enquiryId = Guid.NewGuid();
        await using (var s = h.NewScope())
        {
            s.Db.TripEnquiries.Add(new TripEnquiry
            {
                Id = enquiryId, TripId = trip, TravellerUserId = seeker, OwnerUserId = driver.User, Kind = TripEnquiryKinds.Taxi,
                ListingId = driver.Listing, ListingName = "Ramesh", Status = TripEnquiryStatuses.Asked, ExpiresAt = DateTime.UtcNow.AddDays(1),
                CheckIn = h.Start, CheckOut = h.Start.AddDays(1), CityLeg = 1,
            });
            await s.Db.SaveChangesAsync();
        }

        await using var scope = h.NewScope();
        var result = await Service(scope.Db).CreateAsync(trip, seeker, Body());

        Assert.Equal(TaxiRequestErrorTypes.LegacyTaxiActive, result.Error!.Type);
        var data = (LegacyTaxiActiveData)result.Error.Data!;
        Assert.Equal(enquiryId, Assert.Single(data.Enquiries).Id);
        await using var read = CreateContext();
        Assert.False(await read.Set<TaxiRequest>().AnyAsync(r => r.TripId == trip));
    }

    private const long Almora = 210000077;

    private static CreateTaxiRequestDto SightseeingOnly(params int[] days) => new()
    {
        ClientRequestId = Guid.NewGuid(),
        Pickup = new CreateTaxiPickupDto { On = false },
        Sightseeing = new CreateTaxiSightseeingDto { On = true, Days = days.ToList() },
        Drop = new CreateTaxiDropDto { On = false },
        Seats = 4,
    };

    private static async Task<Guid> TripFromAsync(JourneyHarness h, Guid seeker, int startOffset, string title)
    {
        var plan = h.BuildPlan(("Nainital", Nainital, 3));
        plan.Settings.StartDate = h.Start.AddDays(startOffset);
        var id = await h.AddTripAsync(seeker, plan);
        await using var s = h.NewScope();
        await s.Db.Trips.Where(t => t.Id == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.Title, title));
        return id;
    }

    // O3 + O10
    [IntegrationFact]
    public async Task Create_is_refused_when_another_trip_of_the_traveller_has_an_active_request_on_overlapping_dates()
    {
        await using var h = new JourneyHarness(Db);
        await ReadyDriverAsync(h, "Ramesh");
        var seeker = await h.AddSeekerAsync();
        var hills = await TripFromAsync(h, seeker, 0, "Hills trip");
        var lakes = await TripFromAsync(h, seeker, 2, "Lakes trip");
        var first = await CreateAsync(h, hills, seeker);

        await using var s = h.NewScope();
        var second = await Service(s.Db).CreateAsync(lakes, seeker, Body());

        Assert.Equal((409, TaxiRequestErrorTypes.OverlappingTaxiRequest), (second.Error!.Status, second.Error.Type));
        var data = (OverlappingTaxiRequestData)second.Error.Data!;
        Assert.Equal((hills, "Hills trip", first.Id), (data.TripId, data.TripName, data.RequestId));
        Assert.Equal("You already have a taxi request for these dates in Hills trip", second.Error.Message);
        await using var read = CreateContext();
        Assert.False(await read.Set<TaxiRequest>().AnyAsync(r => r.TripId == lakes));
    }

    [IntegrationFact]
    public async Task Create_is_refused_while_another_trip_has_an_active_booking_on_overlapping_dates()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var seeker = await h.AddSeekerAsync();
        var hills = await TripFromAsync(h, seeker, 0, "Hills trip");
        var lakes = await TripFromAsync(h, seeker, 1, "Lakes trip");
        await using (var s = h.NewScope())
        {
            s.Db.Set<TaxiBooking>().Add(new TaxiBooking
            {
                Id = Guid.NewGuid(), RequestId = Guid.NewGuid(), OfferId = Guid.NewGuid(), TripId = hills, DriverListingId = driver.Listing,
                DriverUserId = driver.User, TravellerUserId = seeker, DateFrom = h.Start, DateTo = h.Start.AddDays(2),
                Status = BookingStates.InProgress, TripName = "Hills trip", BookedAt = DateTime.UtcNow,
            });
            await s.Db.SaveChangesAsync();
        }

        await using var scope = h.NewScope();
        var result = await Service(scope.Db).CreateAsync(lakes, seeker, Body());
        Assert.Equal(TaxiRequestErrorTypes.OverlappingTaxiRequest, result.Error!.Type);
        Assert.Equal("Hills trip", ((OverlappingTaxiRequestData)result.Error.Data!).TripName);
    }

    [IntegrationFact]
    public async Task Another_trip_on_other_dates_or_with_an_ended_request_does_not_block()
    {
        await using var h = new JourneyHarness(Db);
        await ReadyDriverAsync(h, "Ramesh");
        var seeker = await h.AddSeekerAsync();
        var hills = await TripFromAsync(h, seeker, 0, "Hills trip");
        var later = await TripFromAsync(h, seeker, 5, "Later trip");
        var lakes = await TripFromAsync(h, seeker, 1, "Lakes trip");
        var first = await CreateAsync(h, hills, seeker);

        await CreateAsync(h, later, seeker);
        await SetAsync(h, first.Id, RequestStates.Cancelled, 0, 3);
        await using var s = h.NewScope();
        Assert.True((await Service(s.Db).CreateAsync(lakes, seeker, Body())).Created);
    }

    [IntegrationFact]
    public async Task Two_trips_racing_on_overlapping_dates_create_only_one_request()
    {
        await using var h = new JourneyHarness(Db);
        await ReadyDriverAsync(h, "Ramesh");
        var seeker = await h.AddSeekerAsync();
        var hills = await TripFromAsync(h, seeker, 0, "Hills trip");
        var lakes = await TripFromAsync(h, seeker, 0, "Lakes trip");

        async Task<TaxiRequestResult> Run(Guid trip)
        {
            await using var s = h.NewScope();
            return await Service(s.Db).CreateAsync(trip, seeker, Body());
        }

        var results = await Task.WhenAll(Run(hills), Run(lakes));
        Assert.Single(results, r => r.Created);
        Assert.Single(results, r => r.Error?.Type == TaxiRequestErrorTypes.OverlappingTaxiRequest);
    }

    // O4: sightseeing-only days in the second city match drivers of that city, and the empty pool check uses it too.
    [IntegrationFact]
    public async Task Sightseeing_in_the_second_city_dispatches_to_that_city()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var seeker = await h.AddSeekerAsync();
        var trip = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 2), ("Almora", Almora, 2)));

        await using (var s = h.NewScope())
        {
            var refused = await Service(s.Db).CreateAsync(trip, seeker, SightseeingOnly(3, 4));
            Assert.Equal(TaxiRequestErrorTypes.NoDriversNearHere, refused.Error!.Type);
        }

        await using (var s = h.NewScope())
        {
            s.Db.TaxiRoutes.Add(new TaxiRoute
            {
                Id = Guid.NewGuid(), TaxiListingId = driver.Listing, FromName = "Nainital", ToName = "Almora", FromCityKey = Nainital, ToCityKey = Almora,
                FromLatitude = 29.39m, FromLongitude = 79.45m, ToLatitude = 29.59m, ToLongitude = 79.65m, Fare = 2500, TripType = "OneWay",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await s.Db.SaveChangesAsync();
        }

        await using var scope = h.NewScope();
        var created = await Service(scope.Db).CreateAsync(trip, seeker, SightseeingOnly(3, 4));
        Assert.True(created.Created, created.Error?.Type);
        Assert.Equal((Almora, Almora, (long?)null), (created.Request!.DispatchCityKey, created.Request.MatchToCityKey, created.Request.MatchFromCityKey));
    }

    [IntegrationFact]
    public async Task Cancel_closes_offers_and_unlocks()
    {
        await using var h = new JourneyHarness(Db);
        var a = await ReadyDriverAsync(h, "Ramesh");
        var b = await ReadyDriverAsync(h, "Mohan");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);
        await SetAsync(h, request.Id, RequestStates.Collecting, 1, 3);
        var ringing = await OfferAsync(h, request.Id, a, OfferStates.Ringing);
        var accepted = await OfferAsync(h, request.Id, b, OfferStates.Accepted);

        await using (var s = h.NewScope())
        {
            var stranger = await Service(s.Db).CancelAsync(request.Id, Guid.NewGuid(), RequestEndReasons.TravellerCancelled, null);
            Assert.Equal(TaxiRequestErrorTypes.NotTripOwner, stranger.Error!.Type);
        }
        Assert.Equal(RequestStates.Collecting, (await ReadAsync(request.Id)).Status);

        TaxiRequestResult result;
        await using (var s = h.NewScope()) result = await Service(s.Db).CancelAsync(request.Id, seeker, RequestEndReasons.TravellerCancelled, null);

        Assert.Equal(RequestStates.Cancelled, result.Request!.Status);
        Assert.Equal(RequestEndReasons.TravellerCancelled, result.Request.EndReason);
        await using var read = CreateContext();
        var offers = await read.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.RequestId == request.Id).ToListAsync();
        Assert.All(offers, o => Assert.Equal(OfferStates.Closed, o.Status));
        var subjects = new[] { ringing, accepted, request.Id };
        Assert.False(await read.Set<DueAction>().AnyAsync(d => d.State == DueActionStates.Pending && subjects.Contains(d.SubjectId)));
        Assert.False(await read.Set<TaxiRequest>().AnyAsync(r => r.TripId == trip && RequestStates.Active.Contains(r.Status)));

        await using var again = h.NewScope();
        var repeat = await Service(again.Db).CancelAsync(request.Id, seeker, RequestEndReasons.TravellerCancelled, null);
        Assert.True(repeat.IsOk);
    }

    [IntegrationFact]
    public async Task Booked_cancel_needs_a_chip()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);
        await SetAsync(h, request.Id, RequestStates.Booked, 1, 3);
        var offer = await OfferAsync(h, request.Id, driver, OfferStates.Confirmed);
        var bookingId = Guid.NewGuid();
        await using (var s = h.NewScope())
        {
            s.Db.Set<TaxiBooking>().Add(new TaxiBooking
            {
                Id = bookingId, RequestId = request.Id, OfferId = offer, TripId = trip, DriverListingId = driver.Listing, DriverUserId = driver.User,
                TravellerUserId = seeker, DateFrom = request.DateFrom, DateTo = request.DateTo, BookedAt = DateTime.UtcNow,
            });
            await s.Db.SaveChangesAsync();
        }

        await using (var s = h.NewScope())
        {
            var noChip = await Service(s.Db).CancelAsync(request.Id, seeker, RequestEndReasons.TravellerCancelled, null);
            Assert.Equal(TaxiRequestErrorTypes.ReasonRequired, noChip.Error!.Type);
        }
        TaxiRequestResult result;
        await using (var s = h.NewScope()) result = await Service(s.Db).CancelAsync(request.Id, seeker, RequestEndReasons.TravellerCancelled, ReasonChips.ChangedPlan);

        Assert.Equal(RequestEndReasons.BookingCancelledByTraveller, result.Request!.EndReason);
        await using var read = CreateContext();
        var booking = await read.Set<TaxiBooking>().AsNoTracking().SingleAsync(x => x.Id == bookingId);
        Assert.Equal((BookingStates.Cancelled, BookingCancelledBy.Traveller, ReasonChips.ChangedPlan), (booking.Status, booking.CancelledBy, booking.CancelChip));
        Assert.Contains(_pushes.Notices, n => n.Kind == TaxiNoticeKind.BookingCancelled && n.UserId == driver.User);
    }

    [IntegrationFact]
    public async Task Ask_more_tops_up_to_three_and_caps_at_nine()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);

        async Task<TaxiRequestResult> AskAsync()
        {
            await using var s = h.NewScope();
            return await Service(s.Db).AskMoreAsync(request.Id, seeker);
        }

        await SetAsync(h, request.Id, RequestStates.Collecting, 3, 3);
        Assert.Equal(6, (await AskAsync()).Request!.AcceptTarget);
        await SetAsync(h, request.Id, RequestStates.Collecting, 8, 8);
        Assert.Equal(9, (await AskAsync()).Request!.AcceptTarget);
        await SetAsync(h, request.Id, RequestStates.Collecting, 9, 9);
        var capped = await AskAsync();
        Assert.True(capped.IsOk);
        Assert.Equal(9, capped.Request!.AcceptTarget);

        await SetAsync(h, request.Id, RequestStates.Collecting, 3, 3);
        await OfferAsync(h, request.Id, driver, OfferStates.Accepted);
        Assert.Equal(TaxiRequestErrorTypes.AllDriversBusy, (await AskAsync()).Error!.Type);
    }

    // O6: while the chosen driver has his confirm window, Ask more stays closed (even with an exhausted queue).
    [IntegrationFact]
    public async Task Ask_more_is_refused_while_a_driver_is_selected()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        await ReadyDriverAsync(h, "Mohan");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);
        await SetAsync(h, request.Id, RequestStates.Selected, 1, 3);
        var offer = await OfferAsync(h, request.Id, driver, OfferStates.Selected);
        await using (var s = h.NewScope())
            await s.Db.Set<TaxiRequest>().Where(r => r.Id == request.Id).ExecuteUpdateAsync(u => u
                .SetProperty(r => r.SelectedOfferId, offer).SetProperty(r => r.QueueExhaustedAt, DateTime.UtcNow));
        var before = await ReadAsync(request.Id);

        TaxiRequestResult result;
        await using (var s = h.NewScope()) result = await Service(s.Db).AskMoreAsync(request.Id, seeker);

        Assert.Equal((409, TaxiRequestErrorTypes.TaxiRequestStateInvalid), Status(result));
        var after = await ReadAsync(request.Id);
        Assert.Equal((RequestStates.Selected, (Guid?)offer, (short)3, before.Version), (after.Status, after.SelectedOfferId, after.AcceptTarget, after.Version));
        Assert.NotNull(after.QueueExhaustedAt);
    }

    [IntegrationFact]
    public async Task Select_starts_the_fifteen_minute_window()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);
        await SetAsync(h, request.Id, RequestStates.Collecting, 1, 3);
        var offer = await OfferAsync(h, request.Id, driver, OfferStates.Accepted);

        TaxiRequestResult result;
        await using (var s = h.NewScope()) result = await Service(s.Db).SelectAsync(request.Id, offer, seeker);

        Assert.Equal((RequestStates.Selected, (Guid?)offer), (result.Request!.Status, result.Request.SelectedOfferId));
        await using var read = CreateContext();
        var o = await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(x => x.Id == offer);
        Assert.Equal(OfferStates.Selected, o.Status);
        Assert.InRange((o.ConfirmBy!.Value.ToUniversalTime() - DateTime.UtcNow).TotalSeconds, TaxiDispatchRules.ConfirmSeconds - 60, TaxiDispatchRules.ConfirmSeconds + 60);
        Assert.True(await read.Set<DueAction>().AnyAsync(d => d.SubjectId == offer && d.Kind == DueActionKinds.ConfirmExpire && d.State == DueActionStates.Pending));
        Assert.Contains(_pushes.Notices, n => n.Kind == TaxiNoticeKind.Selected && n.OfferId == offer);

        await using var again = h.NewScope();
        Assert.True((await Service(again.Db).SelectAsync(request.Id, offer, seeker)).IsOk);
    }

    [IntegrationFact]
    public async Task Wait_expiry_ends_as_all_drivers_busy()
    {
        await using var h = new JourneyHarness(Db);
        var (trip, seeker) = await TripAsync(h);
        await ReadyDriverAsync(h, "Ramesh");
        var request = await CreateAsync(h, trip, seeker);

        await SetAsync(h, request.Id, RequestStates.Open, 0, 3, DateTime.UtcNow);
        Assert.Equal(DueOutcome.Skipped, await RunHandlerAsync(h, new RequestWaitExpireHandler(Store()), request.Id));

        await SetAsync(h, request.Id, RequestStates.Open, 0, 3, DateTime.UtcNow.AddMinutes(-11));
        Assert.Equal(DueOutcome.Done, await RunHandlerAsync(h, new RequestWaitExpireHandler(Store()), request.Id));
        var ended = await ReadAsync(request.Id);
        Assert.Equal((RequestStates.Expired, RequestEndReasons.AllDriversBusy), (ended.Status, ended.EndReason));
    }

    private static async Task<Guid> LegacyTaxiRowAsync(JourneyHarness h, Guid trip, Guid seeker, (Guid Listing, Guid User) driver, string? contextJson = null, short? day = null)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.TripEnquiries.Add(new TripEnquiry
        {
            Id = id, TripId = trip, TravellerUserId = seeker, OwnerUserId = driver.User, Kind = TripEnquiryKinds.Taxi,
            ListingId = driver.Listing, ListingName = "Ramesh", Status = TripEnquiryStatuses.Asked, ExpiresAt = DateTime.UtcNow.AddDays(1),
            CheckIn = h.Start, CheckOut = h.Start.AddDays(1), CityLeg = 1, Day = day, ContextJson = contextJson,
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    [IntegrationFact]
    public async Task Journey_car_rows_do_not_block_create()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var context = System.Text.Json.JsonSerializer.Serialize(new TripEnquiryContext { City = "Nainital", JourneyId = Guid.NewGuid(), LegIndex = 1 }, TripJson.Options);
        await LegacyTaxiRowAsync(h, trip, seeker, driver, context, day: 1);

        await using var scope = h.NewScope();
        var result = await Service(scope.Db).CreateAsync(trip, seeker, Body());

        Assert.True(result.Created, result.Error?.Type);
        Assert.Equal(RequestStates.Open, result.Request!.Status);
    }

    [IntegrationFact]
    public async Task Not_agreed_last_accepted_driver_reopens_to_open()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var other = await ReadyDriverAsync(h, "Mohan");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);
        await SetAsync(h, request.Id, RequestStates.Collecting, 1, 3);
        var accepted = await OfferAsync(h, request.Id, driver, OfferStates.Accepted);
        var ringing = await OfferAsync(h, request.Id, other, OfferStates.Ringing);

        TaxiRequestResult result;
        await using (var s = h.NewScope()) result = await Service(s.Db).NotAgreedAsync(request.Id, accepted, seeker);

        Assert.True(result.IsOk, result.Error?.Type);
        Assert.Equal(RequestStates.Open, result.Request!.Status);
        await using (var read = CreateContext())
            Assert.Equal(OfferStates.NotAgreed, (await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == accepted)).Status);

        var version = (await ReadAsync(request.Id)).Version;
        await using (var s = h.NewScope())
        {
            var repeat = await Service(s.Db).NotAgreedAsync(request.Id, accepted, seeker);
            Assert.True(repeat.IsOk);
            Assert.Equal(version, repeat.Request!.Version);
        }

        await using (var s = h.NewScope())
        {
            var notAccepted = await Service(s.Db).NotAgreedAsync(request.Id, ringing, seeker);
            Assert.Equal((409, TaxiRequestErrorTypes.OfferStateInvalid), (notAccepted.Error!.Status, notAccepted.Error.Type));
        }
        Assert.Equal(version, (await ReadAsync(request.Id)).Version);
    }

    [IntegrationFact]
    public async Task Not_agreed_with_another_accepted_driver_stays_collecting()
    {
        await using var h = new JourneyHarness(Db);
        var a = await ReadyDriverAsync(h, "Ramesh");
        var b = await ReadyDriverAsync(h, "Mohan");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);
        await SetAsync(h, request.Id, RequestStates.Collecting, 2, 3);
        var first = await OfferAsync(h, request.Id, a, OfferStates.Accepted);
        var second = await OfferAsync(h, request.Id, b, OfferStates.Accepted);

        TaxiRequestResult result;
        await using (var s = h.NewScope()) result = await Service(s.Db).NotAgreedAsync(request.Id, first, seeker);

        Assert.Equal(RequestStates.Collecting, result.Request!.Status);
        await using var read = CreateContext();
        Assert.Equal(OfferStates.Accepted, (await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == second)).Status);
    }

    [IntegrationFact]
    public async Task Admin_cancel_stores_the_note_and_ends_as_admin_cancelled()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);
        await SetAsync(h, request.Id, RequestStates.Collecting, 1, 3);
        var offer = await OfferAsync(h, request.Id, driver, OfferStates.Accepted);

        await using (var s = h.NewScope())
        {
            var blank = await Service(s.Db).CancelByAdminAsync(request.Id, "   ");
            Assert.Equal(400, blank.Error!.Status);
        }
        Assert.Equal(RequestStates.Collecting, (await ReadAsync(request.Id)).Status);

        TaxiRequestResult result;
        await using (var s = h.NewScope()) result = await Service(s.Db).CancelByAdminAsync(request.Id, "  Duplicate request  ");

        Assert.Equal((RequestStates.Cancelled, RequestEndReasons.AdminCancelled, "Duplicate request"),
            (result.Request!.Status, result.Request.EndReason, result.Request.AdminNote));
        await using (var read = CreateContext())
        {
            Assert.Equal(OfferStates.Closed, (await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == offer)).Status);
            Assert.False(await read.Set<DueAction>().AnyAsync(d => d.State == DueActionStates.Pending && (d.SubjectId == offer || d.SubjectId == request.Id)));
        }

        await using (var s = h.NewScope())
        {
            var repeat = await Service(s.Db).CancelByAdminAsync(request.Id, "Another note");
            Assert.True(repeat.IsOk);
            Assert.Equal((result.Request.Version, "Duplicate request"), (repeat.Request!.Version, repeat.Request.AdminNote));
        }
    }

    [IntegrationFact]
    public async Task Admin_can_end_a_running_trip()
    {
        await using var h = new JourneyHarness(Db);
        await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);
        await SetAsync(h, request.Id, RequestStates.InProgress, 1, 3);

        await using var s = h.NewScope();
        var result = await Service(s.Db).CancelByAdminAsync(request.Id, "Driver reported a breakdown");

        Assert.Equal((RequestStates.Cancelled, RequestEndReasons.AdminCancelled), (result.Request!.Status, result.Request.EndReason));
    }

    [IntegrationFact]
    public async Task Every_action_checks_the_owner_and_the_id_before_any_change()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var stranger = await h.AddSeekerAsync();

        await using (var s = h.NewScope())
        {
            var svc = Service(s.Db);
            Assert.Equal((403, TaxiRequestErrorTypes.NotTripOwner), Status(await svc.CreateAsync(trip, stranger, Body())));
            Assert.Equal((404, TripErrorTypes.TripNotFound), Status(await svc.CreateAsync(Guid.NewGuid(), seeker, Body())));
        }
        await using (var read = CreateContext())
            Assert.False(await read.Set<TaxiRequest>().AnyAsync(r => r.TripId == trip));

        var request = await CreateAsync(h, trip, seeker);
        await SetAsync(h, request.Id, RequestStates.Collecting, 1, 3);
        var offer = await OfferAsync(h, request.Id, driver, OfferStates.Accepted);
        var before = await ReadAsync(request.Id);

        await using (var s = h.NewScope())
        {
            var svc = Service(s.Db);
            var denied = (403, TaxiRequestErrorTypes.NotTripOwner);
            Assert.Equal(denied, Status(await svc.CancelAsync(request.Id, stranger, RequestEndReasons.TravellerCancelled, null)));
            Assert.Equal(denied, Status(await svc.AskMoreAsync(request.Id, stranger)));
            Assert.Equal(denied, Status(await svc.SelectAsync(request.Id, offer, stranger)));
            Assert.Equal(denied, Status(await svc.NotAgreedAsync(request.Id, offer, stranger)));

            var missing = (404, TaxiRequestErrorTypes.TaxiRequestNotFound);
            var unknown = Guid.NewGuid();
            Assert.Equal(missing, Status(await svc.CancelAsync(unknown, seeker, RequestEndReasons.TravellerCancelled, null)));
            Assert.Equal(missing, Status(await svc.AskMoreAsync(unknown, seeker)));
            Assert.Equal(missing, Status(await svc.SelectAsync(unknown, offer, seeker)));
            Assert.Equal(missing, Status(await svc.NotAgreedAsync(unknown, offer, seeker)));
            Assert.Equal(missing, Status(await svc.CancelByAdminAsync(unknown, "Note")));

            var noOffer = (404, TaxiRequestErrorTypes.OfferNotFound);
            Assert.Equal(noOffer, Status(await svc.SelectAsync(request.Id, Guid.NewGuid(), seeker)));
            Assert.Equal(noOffer, Status(await svc.NotAgreedAsync(request.Id, Guid.NewGuid(), seeker)));
        }

        var after = await ReadAsync(request.Id);
        Assert.Equal((before.Status, before.Version, before.AcceptTarget), (after.Status, after.Version, after.AcceptTarget));
        await using var check = CreateContext();
        Assert.Equal(OfferStates.Accepted, (await check.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == offer)).Status);
    }

    private static (int, string) Status(TaxiRequestResult result) => (result.Error!.Status, result.Error.Type);

    [IntegrationFact]
    public async Task Stale_expiry_ends_as_not_booked()
    {
        await using var h = new JourneyHarness(Db);
        await ReadyDriverAsync(h, "Ramesh");
        var (trip, seeker) = await TripAsync(h);
        var request = await CreateAsync(h, trip, seeker);

        await SetAsync(h, request.Id, RequestStates.Booked, 1, 3);
        Assert.Equal(DueOutcome.Skipped, await RunHandlerAsync(h, new RequestStaleExpireHandler(Store()), request.Id));

        await SetAsync(h, request.Id, RequestStates.Collecting, 1, 3);
        Assert.Equal(DueOutcome.Done, await RunHandlerAsync(h, new RequestStaleExpireHandler(Store()), request.Id));
        var ended = await ReadAsync(request.Id);
        Assert.Equal((RequestStates.Expired, RequestEndReasons.NotBooked), (ended.Status, ended.EndReason));
    }
}
