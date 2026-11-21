using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be3;

// Contract 11.2, 11.3, 11.4 for lane BE3. Needs RENTNEARBY_TEST_DB (PostGIS); WRITTEN, NOT RUN on this machine.
[Trait("Category", "Integration")]
public class Be3IntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private const long City = 210000001, Gone = 210000077, Kept = 210000088;

    private sealed class Catalog(PlanCatalogSnapshot snapshot) : IPlanCatalogCache
    {
        public Task<PlanCatalogSnapshot> GetAsync() => Task.FromResult(snapshot);
        public Task<PlanCatalogSnapshot> RefreshAsync() => Task.FromResult(snapshot);
        public void Invalidate() { }
    }

    private sealed class Payment(bool on) : IPaymentFeatureReader
    {
        public Task<PaymentFeatureState> GetAsync() => Task.FromResult(on ? PaymentFeatureStates.On() : PaymentFeatureStates.Off());
        public void Invalidate() { }
    }

    private sealed class RecordingNotifier : IOwnerNotifier
    {
        public List<NoticeUpsert> Stored { get; } = new();
        public List<OwnerPush> Pushed { get; } = new();
        public List<(string Kind, Guid Entity, Guid[] Users)> Cleared { get; } = new();
        public Task StoreAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default) { Stored.AddRange(notices); return Task.CompletedTask; }
        public Task ClearAsync(string kind, Guid entityId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) { Cleared.Add((kind, entityId, [.. userIds])); return Task.CompletedTask; }
        public Task PushAsync(IReadOnlyCollection<OwnerPush> pushes, CancellationToken ct = default) { Pushed.AddRange(pushes); return Task.CompletedTask; }
        public void Wake() { }
    }

    private sealed class NullSignals : IOutboxSignal, IDueActionsSignal, IMatcherSignal, ITaxiRequestEvents
    {
        void IOutboxSignal.Wake() { }
        void IDueActionsSignal.Wake() { }
        public void MarkDirty(long cityKey) { }
        public void MarkDriverFreed(Guid driverListingId) { }
        public Task RequestChangedAsync(Guid requestId, Guid tripId, Guid travellerUserId, long version, string state) => Task.CompletedTask;
    }

    private sealed class RecordingPushWriter : ITaxiPushWriter
    {
        public List<RingEndPush> RingEnds { get; } = new();
        public Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default) => Task.CompletedTask;
        public Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default) { RingEnds.Add(ringEnd); return Task.CompletedTask; }
        public Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static TaxiRequestClosure Closure(ApplicationDbContext db, RecordingPushWriter writer)
    {
        var s = new NullSignals();
        var store = new TaxiRequestStore(writer, new DueActionScheduler(), s, s, s, s, NullLogger<TaxiRequestStore>.Instance);
        return new TaxiRequestClosure(store, writer, new DueActionScheduler(), new TaxiPickupPlaces(db, null!), NullLogger<TaxiRequestClosure>.Instance);
    }

    private static PlanLimitsReconcile Reconcile(ApplicationDbContext db, bool paymentOn, RecordingNotifier notifier, RecordingPushWriter? writer = null)
        => new(db, new Catalog(OwnerPlanCatalog.Seeded()), new Payment(paymentOn), Closure(db, writer ?? new RecordingPushWriter()), notifier,
            NullLogger<PlanLimitsReconcile>.Instance);

    private async Task<(List<StayRoom> Rooms, List<StayPhoto> Photos)> RowsAsync(Guid stayId)
    {
        await using var c = CreateContext();
        return (await c.StayRooms.IgnoreQueryFilters().AsNoTracking().Where(r => r.StayListingId == stayId).OrderBy(r => r.RoomIndex).ToListAsync(),
            await c.StayPhotos.IgnoreQueryFilters().AsNoTracking().Where(p => p.StayListingId == stayId).OrderBy(p => p.PhotoOrder).ToListAsync());
    }

    [IntegrationFact]
    public async Task Expiry_hides_extra_rows_and_plus_again_shows_them_with_the_bands()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.RoomsAsync(c, owner.ListingId, visible: 4);
            await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 5);
        }

        await using (var c = CreateContext())
        {
            await using var tx = await c.Database.BeginTransactionAsync();
            await c.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {owner.UserId} FOR UPDATE");
            var effect = await Reconcile(c, true, new RecordingNotifier()).ApplyForOwnerAsync(owner.UserId, ListingKinds.Stay, Be3Kit.Free(ListingKinds.Stay));
            await tx.CommitAsync();
            Assert.Equal((0, 5), (effect.HiddenBefore, effect.HiddenAfter));
        }
        var (rooms, photos) = await RowsAsync(owner.ListingId);
        Assert.Equal([1, 2, 101, 102], rooms.Select(r => (int)r.RoomIndex));
        Assert.Equal([false, false, true, true], rooms.Select(r => r.HiddenByPlan));
        Assert.Equal(3, photos.Count(p => p.HiddenByPlan));
        await using (var c = CreateContext())
        {
            Assert.Equal(2, await c.StayRooms.CountAsync(r => r.StayListingId == owner.ListingId));
            Assert.Equal(3000, (await c.StayListings.AsNoTracking().SingleAsync(s => s.Id == owner.ListingId)).MinPricePerNight);
        }

        await using (var c = CreateContext())
        {
            await using var tx = await c.Database.BeginTransactionAsync();
            await Reconcile(c, true, new RecordingNotifier()).ApplyForOwnerAsync(owner.UserId, ListingKinds.Stay, Be3Kit.Plus(ListingKinds.Stay));
            await tx.CommitAsync();
        }
        (rooms, photos) = await RowsAsync(owner.ListingId);
        Assert.Equal([1, 2, 3, 4], rooms.Select(r => (int)r.RoomIndex));
        Assert.DoesNotContain(photos, p => p.HiddenByPlan);
    }

    [IntegrationFact]
    public async Task Payment_off_to_on_notifies_only_owners_who_lost_something_and_on_to_off_clears()
    {
        OwnerSeed.OwnerRow big, small;
        await using (var c = CreateContext())
        {
            big = await OwnerSeed.StayOwnerAsync(c);
            small = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.PhotosAsync(c, big.ListingId, visible: 6);
            await OwnerSeed.RoomsAsync(c, big.ListingId, visible: 3);
            await OwnerSeed.PhotosAsync(c, small.ListingId, visible: 2);
            await OwnerSeed.RoomsAsync(c, small.ListingId, visible: 1);
        }

        var notifier = new RecordingNotifier();
        await using (var c = CreateContext())
        {
            var result = await Reconcile(c, true, notifier).ReconcileAsync(ListingKinds.Stay);
            Assert.Equal(1, result.Notified);
        }
        Assert.Equal(big.UserId, Assert.Single(notifier.Stored).UserId);
        var push = Assert.Single(notifier.Pushed);
        Assert.Equal("4 photos and 1 room type are hidden on Free. Nothing is deleted.", push.Body);

        await using (var c = CreateContext())
            await Reconcile(c, false, new RecordingNotifier()).ReconcileAsync(ListingKinds.Stay);
        var (rooms, photos) = await RowsAsync(big.ListingId);
        Assert.DoesNotContain(rooms, r => r.HiddenByPlan);
        Assert.DoesNotContain(photos, p => p.HiddenByPlan);
    }

    [IntegrationFact]
    public async Task Choose_what_stays_saves_counts_once_and_refuses_the_fourth_change()
    {
        OwnerSeed.OwnerRow owner;
        List<StayPhoto> photos;
        List<StayRoom> rooms;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            rooms = await OwnerSeed.RoomsAsync(c, owner.ListingId, visible: 2, hidden: 1);
            photos = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 2, hidden: 2);
            await OwnerSeed.ActivePlanAsync(c, owner.UserId, ListingKinds.Stay, OwnerPlanCatalog.StayFreeId);
        }

        async Task<PlanVisibleOutcome> SaveAsync(Guid[] photoIds, Guid[] roomIds)
        {
            await using var c = CreateContext();
            var entitlements = new PlanEntitlementService(c, new Catalog(OwnerPlanCatalog.Seeded()), new Payment(true));
            var service = new PlanVisibleService(c, entitlements, Closure(c, new RecordingPushWriter()), new RecordingNotifier(), TimeProvider.System,
                NullLogger<PlanVisibleService>.Instance);
            return await service.SaveStayAsync(owner.UserId, photoIds, roomIds);
        }

        var same = await SaveAsync([photos[0].Id, photos[1].Id], [rooms[0].Id, rooms[1].Id]);
        Assert.False(same.Changed);
        Assert.Equal(0, same.SavesUsedToday);

        Assert.True((await SaveAsync([photos[3].Id, photos[0].Id], [rooms[2].Id, rooms[0].Id])).Changed);
        var (_, after) = await RowsAsync(owner.ListingId);
        Assert.Equal(photos[3].Id, after.Where(p => !p.HiddenByPlan).OrderBy(p => p.PhotoOrder).First().Id);

        Assert.True((await SaveAsync([photos[1].Id, photos[0].Id], [rooms[1].Id, rooms[0].Id])).Changed);
        Assert.True((await SaveAsync([photos[2].Id, photos[0].Id], [rooms[2].Id, rooms[0].Id])).Changed);
        var fourth = await SaveAsync([photos[3].Id, photos[0].Id], [rooms[1].Id, rooms[0].Id]);
        Assert.Equal(OwnerErrorTypes.PlanVisibleLimit, fourth.Error);

        var invalid = await SaveAsync([photos[0].Id], [rooms[0].Id, rooms[1].Id]);
        Assert.Equal(OwnerErrorTypes.PlanVisibleInvalid, invalid.Error);
    }

    [IntegrationFact]
    public async Task Hiding_a_route_closes_its_ringing_and_accepted_offers_with_the_plan_reason_and_keeps_selected()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var user = await h.AddTaxiDriverAsync("Plan driver");
        var listing = await h.AddTaxiAsync(user);
        await using (var s = h.NewScope())
        {
            var now = DateTime.UtcNow;
            s.Db.TaxiRoutes.AddRange(
                Route(listing, 1, Kept, now), Route(listing, 2, Gone, now));
            await s.Db.SaveChangesAsync();
        }
        var ringing = await OfferAsync(h, await RequestAsync(h, traveller, RequestStates.Open, Gone), listing, user, OfferStates.Ringing);
        var selected = await OfferAsync(h, await RequestAsync(h, traveller, RequestStates.Selected, Gone, accepted: 1), listing, user, OfferStates.Selected);

        var writer = new RecordingPushWriter();
        await using (var s = h.NewScope())
        {
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            await s.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {user} FOR UPDATE");
            await Reconcile(s.Db, true, new RecordingNotifier(), writer).ApplyForOwnerAsync(user, ListingKinds.Taxi, Be3Kit.Free(ListingKinds.Taxi));
            await s.Db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await using var read = CreateContext();
        var closed = await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == ringing);
        Assert.Equal(OfferEndReasons.RoutePausedByPlan, closed.EndReason);
        Assert.Equal(OfferStates.Selected, (await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == selected)).Status);
        Assert.Equal(OfferEndReasons.RoutePausedByPlan, Assert.Single(writer.RingEnds).Reason);
        Assert.Equal(1, await read.TaxiRoutes.CountAsync(r => r.TaxiListingId == listing));
        Assert.Equal(0, (await read.TaxiListings.AsNoTracking().SingleAsync(l => l.Id == listing)).RingMissStreak);
    }

    [IntegrationFact]
    public async Task Taxi_sent_back_to_review_closes_ringing_offers_only()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var user = await h.AddTaxiDriverAsync("Review driver");
        var listing = await h.AddTaxiAsync(user);
        var ringing = await OfferAsync(h, await RequestAsync(h, traveller, RequestStates.Open, City), listing, user, OfferStates.Ringing);
        var accepted = await OfferAsync(h, await RequestAsync(h, traveller, RequestStates.Collecting, City, accepted: 1), listing, user, OfferStates.Accepted);

        var writer = new RecordingPushWriter();
        await using (var s = h.NewScope())
        {
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            await Closure(s.Db, writer).ListingInReviewAsync(s.Db, listing);
            await s.Db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await using var read = CreateContext();
        Assert.Equal(OfferEndReasons.ListingInReview, (await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == ringing)).EndReason);
        Assert.Equal(OfferStates.Accepted, (await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == accepted)).Status);
        Assert.Equal("This request is closed", OfferEndReasons.RingEndBody(Assert.Single(writer.RingEnds).Reason));
    }

    // 11.3: M3 extra statement and the hub counts stay one statement for 3 and for 30 rows.
    // Writes the real notice and outbox rows, then fails: the business change and the outbox row must roll back together (NFR 1.5).
    private sealed class FailAfterPushNotifier(IOwnerNotifier inner) : IOwnerNotifier
    {
        public Task StoreAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default) => inner.StoreAsync(notices, ct);
        public Task ClearAsync(string kind, Guid entityId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) => inner.ClearAsync(kind, entityId, userIds, ct);
        public async Task PushAsync(IReadOnlyCollection<OwnerPush> pushes, CancellationToken ct = default)
        {
            await inner.PushAsync(pushes, ct);
            throw new InvalidOperationException("push failed after the outbox insert");
        }
        public void Wake() { }
    }

    [IntegrationFact]
    public async Task Stay_change_approve_and_its_outbox_row_commit_together_or_not_at_all()
    {
        OwnerSeed.OwnerRow owner;
        DateTime pendingAt;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            var now = DateTime.UtcNow;
            c.DeviceTokens.Add(new RentNearBy.Core.Entities.DeviceToken
            {
                Id = Guid.NewGuid(), UserId = owner.UserId, Token = "tok-" + Guid.NewGuid().ToString("N"), IsValid = true, CreatedAt = now, UpdatedAt = now,
            });
            var stay = await c.StayListings.SingleAsync(s => s.Id == owner.ListingId);
            var asked = new StayReviewValues("Pine View Stay", stay.RegistrationTypeId, stay.RegistrationTypeName, stay.RegistrationOtherName,
                stay.RegistrationNumber, stay.Gstin, stay.Latitude, stay.Longitude, stay.Address, stay.DistrictId, null, stay.CityId, null);
            stay.PendingEditJson = StayPendingEditJson.Write(new StayPendingEdit([StayReviewFields.Name], asked));
            stay.PendingEditAt = now;
            await c.SaveChangesAsync();
            pendingAt = (await c.StayListings.AsNoTracking().SingleAsync(s => s.Id == owner.ListingId)).PendingEditAt!.Value;
        }

        await using (var c = CreateContext())
        {
            var real = new RentNearBy.Infrastructure.Services.Owner.Be4.OwnerNotifier(c, new RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeWriter(c));
            var service = new StayPendingEditService(c, new FailAfterPushNotifier(real), TimeProvider.System, NullLogger<StayPendingEditService>.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(owner.ListingId, pendingAt));
        }

        await using (var c = CreateContext())
        {
            var stay = await c.StayListings.AsNoTracking().SingleAsync(s => s.Id == owner.ListingId);
            Assert.NotNull(stay.PendingEditJson);
            Assert.Equal("Lake View Stay", stay.Name);
            Assert.Null(stay.PendingEditResult);
            var outbox = await c.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM \"OutboxMessages\" WHERE \"Key\" LIKE {"stayedit:" + owner.ListingId + ":%"}").SingleAsync();
            Assert.Equal(0, outbox);
            Assert.False(await c.Set<RentNearBy.Core.Entities.Buddy.UserNotice>().AnyAsync(n => n.UserId == owner.UserId && n.Kind == NoticeKinds.StayEditApproved));
        }
    }

    [IntegrationFact]
    public async Task Hidden_counts_QueryCount_IsFixed()
    {
        var userId = Guid.Empty;
        await QueryAssert.SameCountAsync(Db,
            async (c, n) =>
            {
                var owner = await OwnerSeed.StayOwnerAsync(c);
                userId = owner.UserId;
                await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 2, hidden: Math.Min(n, 10));
                await OwnerSeed.RoomsAsync(c, owner.ListingId, visible: 2, hidden: Math.Min(n, 5));
            },
            c => OwnerHiddenCounts.ReadAsync(c, userId, ListingKinds.Stay, IstTime.TodayIst(DateTime.UtcNow)),
            budget: 1);
    }

    // 11.3: the reconcile batch is a fixed number of statements for 3 and for 30 listings.
    [IntegrationFact]
    public async Task Reconcile_batch_QueryCount_IsFixed()
    {
        await QueryAssert.SameCountAsync(Db,
            async (c, n) =>
            {
                for (var i = 0; i < n; i++) // batch-loop (seed only)
                {
                    var owner = await OwnerSeed.StayOwnerAsync(c);
                    await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 4);
                }
            },
            c => Reconcile(c, true, new RecordingNotifier()).ReconcileAsync(ListingKinds.Stay),
            budget: 14, small: 3, large: 30);
    }

    private static TaxiRoute Route(Guid listing, short index, long toKey, DateTime now) => new()
    {
        Id = Guid.NewGuid(), TaxiListingId = listing, RouteIndex = index, FromName = "Kathgodam", ToName = "Nainital",
        FromLatitude = 29.27m, FromLongitude = 79.54m, ToLatitude = 29.39m, ToLongitude = 79.45m, FromCityKey = City, ToCityKey = toKey,
        Fare = 1500, TripType = TaxiTripTypes.All[0], CreatedAt = now, UpdatedAt = now,
    };

    private static async Task<Guid> RequestAsync(JourneyHarness h, Guid traveller, string status, long matchTo, int accepted = 0)
    {
        var tripId = await h.AddTripAsync(traveller, h.BuildPlan(("Nainital", City, 3)));
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequest>().Add(new TaxiRequest
        {
            Id = id, TripId = tripId, TravellerUserId = traveller, Status = status, WantSightseeing = true, DispatchCityKey = matchTo,
            MatchToCityKey = matchTo, MatchFromCityKey = City, Seats = 4, Travellers = 2, AcceptedCount = (short)accepted,
            AcceptTarget = (short)TaxiDispatchRules.DefaultAcceptTarget,
            DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)), DateTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(22)),
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> OfferAsync(JourneyHarness h, Guid requestId, Guid listing, Guid user, string status)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequestOffer>().Add(new TaxiRequestOffer
        {
            Id = id, RequestId = requestId, DriverListingId = listing, DriverUserId = user, Status = status, SeqNo = 1,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5), ConfirmBy = status == OfferStates.Selected ? DateTime.UtcNow.AddMinutes(15) : null,
        });
        if (status == OfferStates.Ringing)
            s.Db.Set<DueAction>().Add(new DueAction { Id = Guid.NewGuid(), Kind = DueActionKinds.OfferExpire, SubjectId = id, RequestId = requestId, DueAt = DateTime.UtcNow.AddMinutes(5) });
        if (status == OfferStates.Selected)
            await s.Db.Set<TaxiRequest>().Where(r => r.Id == requestId).ExecuteUpdateAsync(u => u.SetProperty(r => r.SelectedOfferId, id));
        await s.Db.SaveChangesAsync();
        return id;
    }
}
