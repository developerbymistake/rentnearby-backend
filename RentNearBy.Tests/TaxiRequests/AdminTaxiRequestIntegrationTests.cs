using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Written for a real PostgreSQL with PostGIS (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class AdminTaxiRequestIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private const long Nainital = 210000001, Kathgodam = 210000050;

    private sealed class Signals : IOutboxSignal, IDueActionsSignal, IMatcherSignal, ITaxiRequestEvents
    {
        void IOutboxSignal.Wake() { }
        void IDueActionsSignal.Wake() { }
        public void MarkDirty(long cityKey) { }
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

    private TaxiRequestService Service(ApplicationDbContext db)
    {
        var store = new TaxiRequestStore(_pushes, new DueActionScheduler(), _signals, _signals, _signals, _signals, NullLogger<TaxiRequestStore>.Instance);
        return new(db, store, new TaxiPickupPlaces(db, new Resolver()), new DueActionScheduler(), _pushes, new InMemoryRateLimitService(),
            NullLogger<TaxiRequestService>.Instance);
    }

    private static CreateTaxiRequestDto Body() => new()
    {
        ClientRequestId = Guid.NewGuid(),
        Pickup = new CreateTaxiPickupDto { On = true, CityKey = Kathgodam },
        Sightseeing = new CreateTaxiSightseeingDto { On = true, Days = [1, 2] },
        Drop = new CreateTaxiDropDto { On = true, SameAsPickup = true },
        Seats = 4,
        DayStats = [new TaxiDayStatDto { Day = 1, Km = 25, Minutes = 300 }],
    };

    private static async Task<(Guid Listing, Guid User)> ReadyDriverAsync(JourneyHarness h, string name)
    {
        var user = await h.AddTaxiDriverAsync(name);
        var listing = await h.AddTaxiAsync(user);
        await using var s = h.NewScope();
        await s.Db.TaxiListings.Where(l => l.Id == listing).ExecuteUpdateAsync(u => u
            .SetProperty(l => l.Seats, (short)4).SetProperty(l => l.RingReadyAt, DateTime.UtcNow).SetProperty(l => l.RingNotificationsOn, true));
        s.Db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = user, Token = "t-" + Guid.NewGuid(), IsValid = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await s.Db.SaveChangesAsync();
        return (listing, user);
    }

    private async Task<TaxiRequest> RequestAsync(JourneyHarness h, Guid seeker)
    {
        var trip = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 3)));
        await using var s = h.NewScope();
        var r = await Service(s.Db).CreateAsync(trip, seeker, Body());
        Assert.True(r.IsOk, r.Error?.Type);
        return r.Request!;
    }

    private static async Task SetAsync(JourneyHarness h, Guid requestId, string status, short accepted, DateTime createdAt, DateTime updatedAt)
    {
        await using var s = h.NewScope();
        await s.Db.Set<TaxiRequest>().Where(r => r.Id == requestId).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Status, status).SetProperty(r => r.AcceptedCount, accepted)
            .SetProperty(r => r.CreatedAt, createdAt).SetProperty(r => r.UpdatedAt, updatedAt));
    }

    private static async Task<Guid> OfferAsync(JourneyHarness h, Guid requestId, (Guid Listing, Guid User) driver, string status, short seq,
        DateTime offeredAt, DateTime? pushReceivedAt = null, DateTime? respondedAt = null, DateTime? closedAt = null)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequestOffer>().Add(new TaxiRequestOffer
        {
            Id = id, RequestId = requestId, DriverListingId = driver.Listing, DriverUserId = driver.User, Status = status, SeqNo = seq,
            OfferedAt = offeredAt, ExpiresAt = offeredAt.AddMinutes(5), PushReceivedAt = pushReceivedAt, RespondedAt = respondedAt, ClosedAt = closedAt,
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> BookingAsync(JourneyHarness h, TaxiRequest request, Guid offer, (Guid Listing, Guid User) driver, Guid seeker, DateTime bookedAt)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiBooking>().Add(new TaxiBooking
        {
            Id = id, RequestId = request.Id, OfferId = offer, TripId = request.TripId, DriverListingId = driver.Listing, DriverUserId = driver.User,
            TravellerUserId = seeker, DateFrom = request.DateFrom, DateTo = request.DateTo, BookedAt = bookedAt,
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    private async Task<string> PhoneOfAsync(Guid userId)
    {
        await using var read = CreateContext();
        return await read.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.PhoneNumber).SingleAsync();
    }

    private async Task<AdminTaxiHealthDto> HealthAsync(DateTime now)
    {
        await using var context = CreateContext();
        return await AdminTaxiRequestHandlers.BuildHealthAsync(context, new WorkerHeartbeats(), now, default);
    }

    [IntegrationFact]
    public async Task List_pages_walk_every_row_once_with_tied_created_at()
    {
        await using var h = new JourneyHarness(Db);
        await ReadyDriverAsync(h, "Ramesh");
        var seeker = await h.AddSeekerAsync("Aarav Pager");
        var t0 = DateTime.UtcNow.AddMinutes(-2);
        t0 = new DateTime(t0.Ticks - t0.Ticks % 10, DateTimeKind.Utc);
        var created = new[] { t0, t0.AddSeconds(-30), t0.AddSeconds(-30), t0.AddSeconds(-30), t0.AddSeconds(-60) };
        var ids = new List<Guid>();
        foreach (var at in created)
        {
            var r = await RequestAsync(h, seeker);
            await SetAsync(h, r.Id, RequestStates.Open, 0, at, at);
            ids.Add(r.Id);
        }
        var phone = await PhoneOfAsync(seeker);

        List<Guid> expected;
        await using (var read = CreateContext())
            expected = await read.Set<TaxiRequest>().AsNoTracking().Where(r => ids.Contains(r.Id))
                .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Select(r => r.Id).ToListAsync();
        Assert.Equal(5, expected.Count);

        foreach (var size in new[] { 1, 2, 3, 5 })
        {
            var seen = new List<Guid>();
            string? cursor = null;
            for (var guard = 0; guard < 10; guard++)
            {
                await using var context = CreateContext();
                var result = await AdminTaxiRequestHandlers.GetRequests("all", null, "+" + phone, cursor, size, context, default);
                var page = JourneyHarness.DataOf<AdminTaxiRequestListDto>(result);
                Assert.True(page.Items.Count <= size);
                seen.AddRange(page.Items.Select(i => i.Id));
                cursor = page.NextCursor;
                if (cursor is null) break;
            }
            Assert.Equal(expected, seen);
        }

        await using (var context = CreateContext())
        {
            var byName = JourneyHarness.DataOf<AdminTaxiRequestListDto>(
                await AdminTaxiRequestHandlers.GetRequests("active", null, "aarav pag", null, 50, context, default));
            Assert.True(ids.All(id => byName.Items.Any(i => i.Id == id)));
            Assert.All(byName.Items.Where(i => ids.Contains(i.Id)), i => Assert.Equal(phone, i.TravellerPhone));
        }
        await using (var context = CreateContext())
        {
            var ended = JourneyHarness.DataOf<AdminTaxiRequestListDto>(
                await AdminTaxiRequestHandlers.GetRequests("ended", null, phone, null, 50, context, default));
            Assert.Empty(ended.Items);
        }
    }

    [IntegrationFact]
    public async Task Health_counts_move_by_the_seeded_rows()
    {
        await using var h = new JourneyHarness(Db);
        var before = await HealthAsync(DateTime.UtcNow);

        var a = await ReadyDriverAsync(h, "Ramesh");
        var b = await ReadyDriverAsync(h, "Mohan");
        var seeker = await h.AddSeekerAsync();
        var now = DateTime.UtcNow;
        var collecting = await RequestAsync(h, seeker);
        var booked = await RequestAsync(h, seeker);
        var stuck = await RequestAsync(h, seeker);
        await SetAsync(h, collecting.Id, RequestStates.Collecting, 0, now, now);
        await SetAsync(h, booked.Id, RequestStates.Booked, 1, now, now);
        await SetAsync(h, stuck.Id, RequestStates.Open, 0, now.AddMinutes(-1), now.AddMinutes(-20));

        await OfferAsync(h, collecting.Id, a, OfferStates.Ringing, 1, now, pushReceivedAt: now.AddSeconds(2));
        await OfferAsync(h, collecting.Id, b, OfferStates.Closed, 2, now, closedAt: now);
        var confirmed = await OfferAsync(h, booked.Id, a, OfferStates.Confirmed, 1, now, respondedAt: now);
        await BookingAsync(h, booked, confirmed, a, seeker, now);

        var after = await HealthAsync(DateTime.UtcNow);

        Assert.Equal(before.StuckRequests + 1, after.StuckRequests);
        Assert.Equal(before.RequestsToday + 3, after.RequestsToday);
        Assert.Equal(before.ByState.Collecting + 1, after.ByState.Collecting);
        Assert.Equal(before.ByState.Booked + 1, after.ByState.Booked);
        Assert.Equal(before.RingsSentLastHour + 3, after.RingsSentLastHour);
        Assert.Equal(before.RingsAckedLastHour + 1, after.RingsAckedLastHour);
        Assert.NotNull(after.AckP95SecondsLastHour);
        Assert.Equal(before.DriversRingReady + 2, after.DriversRingReady);
        Assert.Equal(before.BookedThisWeek + 1, after.BookedThisWeek);
        Assert.InRange(after.OffersAnsweredPct7d ?? -1, 0, 100);
        Assert.True(after.PendingDueActions >= 1, "create schedules the stale expiry");
    }

    [IntegrationFact]
    public async Task Admin_cancel_ends_the_booking_tells_both_sides_and_unlocks_the_plan()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var seeker = await h.AddSeekerAsync();
        var request = await RequestAsync(h, seeker);
        var now = DateTime.UtcNow;
        await SetAsync(h, request.Id, RequestStates.Booked, 1, now, now);
        var offer = await OfferAsync(h, request.Id, driver, OfferStates.Confirmed, 1, now, respondedAt: now);
        var bookingId = await BookingAsync(h, request, offer, driver, seeker, now);

        await using (var s = h.NewScope())
        {
            var refused = await AdminTaxiRequestHandlers.CancelRequest(request.Id, new AdminCancelTaxiRequestDto { Note = "  " }, Service(s.Db), s.Db, default);
            Assert.Equal(400, JourneyHarness.StatusOf(refused));
        }

        AdminTaxiRequestDetailDto detail;
        await using (var s = h.NewScope())
        {
            var result = await AdminTaxiRequestHandlers.CancelRequest(request.Id, new AdminCancelTaxiRequestDto { Note = "Duplicate request" }, Service(s.Db), s.Db, default);
            Assert.Equal(200, JourneyHarness.StatusOf(result));
            detail = JourneyHarness.DataOf<AdminTaxiRequestDetailDto>(result);
        }

        Assert.Equal((RequestStates.Cancelled, RequestEndReasons.AdminCancelled, "Duplicate request", false),
            (detail.State, detail.EndReason, detail.AdminNote, detail.CanCancel));
        Assert.Equal((BookingStates.Cancelled, BookingCancelledBy.Admin), (detail.Booking!.State, detail.Booking.CancelledBy));
        await using (var read = CreateContext())
        {
            var booking = await read.Set<TaxiBooking>().AsNoTracking().SingleAsync(x => x.Id == bookingId);
            Assert.Equal((BookingStates.Cancelled, BookingCancelledBy.Admin), (booking.Status, booking.CancelledBy));
            Assert.False(await read.Set<TaxiRequest>().AnyAsync(r => r.TripId == request.TripId && RequestStates.Active.Contains(r.Status)));
        }
        Assert.Contains(_pushes.Notices, n => n.Kind == TaxiNoticeKind.RequestEnded && n.UserId == seeker);
        Assert.Contains(_pushes.Notices, n => n.Kind == TaxiNoticeKind.BookingCancelled && n.UserId == driver.User);

        await using (var context = CreateContext())
        {
            var bookings = JourneyHarness.DataOf<AdminTaxiBookingListDto>(await AdminTaxiRequestHandlers.GetBookings("ended", null, 50, context, default));
            Assert.Contains(bookings.Items, x => x.BookingId == bookingId && x.CancelledBy == BookingCancelledBy.Admin);
        }
    }

    [IntegrationFact]
    public async Task Driver_detail_has_rating_offer_counts_and_ring_ready()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ReadyDriverAsync(h, "Ramesh");
        var seeker = await h.AddSeekerAsync();
        var request = await RequestAsync(h, seeker);
        var now = DateTime.UtcNow;
        await OfferAsync(h, request.Id, driver, OfferStates.Missed, 1, now.AddMinutes(-30), closedAt: now.AddMinutes(-25));
        await OfferAsync(h, request.Id, driver, OfferStates.Missed, 2, now.AddMinutes(-20), closedAt: now.AddMinutes(-15));
        await OfferAsync(h, request.Id, driver, OfferStates.Accepted, 3, now.AddMinutes(-10), respondedAt: now.AddMinutes(-9));
        await OfferAsync(h, request.Id, driver, OfferStates.Declined, 4, now.AddMinutes(-5), respondedAt: now.AddMinutes(-4), closedAt: now.AddMinutes(-4));
        await using (var s = h.NewScope())
            await s.Db.TaxiListings.Where(l => l.Id == driver.Listing).ExecuteUpdateAsync(u => u
                .SetProperty(l => l.RatingSum, 346).SetProperty(l => l.RatingCount, 72).SetProperty(l => l.RidesCompleted, 70));

        var cipher = new AesGcmIdentityCipher(new ConfigurationBuilder().Build(), NullLogger<AesGcmIdentityCipher>.Instance);
        AdminTaxiDto dto;
        await using (var context = CreateContext())
        {
            var result = await AdminTaxiHandlers.GetTaxiById(driver.Listing, new UnitOfWork(context), cipher, NullLoggerFactory.Instance, context, new Resolver());
            dto = JourneyHarness.DataOf<AdminTaxiDto>(result);
        }
        Assert.Equal(((double?)4.8, 72, 70), (dto.RatingAverage, dto.RatingCount, dto.RidesCompleted));
        Assert.Equal(((int?)2, (int?)1), (dto.OffersMissed, dto.OffersAccepted));
        Assert.True(dto.RingReady == true);

        await using (var s = h.NewScope())
            await s.Db.DeviceTokens.Where(t => t.UserId == driver.User).ExecuteUpdateAsync(u => u.SetProperty(t => t.IsValid, false));
        await using (var context = CreateContext())
        {
            var result = await AdminTaxiHandlers.GetTaxiById(driver.Listing, new UnitOfWork(context), cipher, NullLoggerFactory.Instance, context, new Resolver());
            Assert.False(JourneyHarness.DataOf<AdminTaxiDto>(result).RingReady == true);
        }
    }

    [IntegrationFact]
    public async Task Unknown_ids_and_bad_cursor()
    {
        await using var context = CreateContext();
        Assert.Equal(404, JourneyHarness.StatusOf(await AdminTaxiRequestHandlers.GetRequest(Guid.NewGuid(), context, default)));
        Assert.Equal(400, JourneyHarness.StatusOf(await AdminTaxiRequestHandlers.GetRequests("all", null, null, "garbage", 5, context, default)));
        Assert.Equal(400, JourneyHarness.StatusOf(await AdminTaxiRequestHandlers.GetBookings("all", "garbage", 5, context, default)));
    }
}
