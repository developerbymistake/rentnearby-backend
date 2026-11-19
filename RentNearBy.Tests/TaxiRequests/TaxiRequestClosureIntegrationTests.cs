using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one. MASTER_PLAN B2-8, one case per closure rule.
[Trait("Category", "Integration")]
public class TaxiRequestClosureIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
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
        public List<TaxiNotice> Notices { get; } = new();
        public Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default) => Task.CompletedTask;
        public Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default) { RingEnds.Add(ringEnd); return Task.CompletedTask; }
        public Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default) { Notices.Add(notice); return Task.CompletedTask; }
    }

    private static TaxiRequestClosure Closure(ApplicationDbContext db, RecordingPushWriter writer)
    {
        var s = new NullSignals();
        var scheduler = new DueActionScheduler();
        var store = new TaxiRequestStore(writer, scheduler, s, s, s, s, NullLogger<TaxiRequestStore>.Instance);
        return new TaxiRequestClosure(store, writer, scheduler, new TaxiPickupPlaces(db, null!), NullLogger<TaxiRequestClosure>.Instance);
    }

    private sealed record Driver(Guid Listing, Guid User);

    private static async Task<Driver> AddDriverAsync(JourneyHarness h, string name)
    {
        var user = await h.AddTaxiDriverAsync(name);
        return new Driver(await h.AddTaxiAsync(user), user);
    }

    private static async Task<(Guid RequestId, Guid TripId)> AddRequestAsync(JourneyHarness h, Guid travellerId, string status, int acceptedCount = 0)
    {
        var tripId = await h.AddTripAsync(travellerId, h.BuildPlan(("Nainital", 210000001, 3)));
        var requestId = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequest>().Add(new TaxiRequest
        {
            Id = requestId, TripId = tripId, TravellerUserId = travellerId, Status = status, WantSightseeing = true,
            DispatchCityKey = 210000001, MatchToCityKey = 210000001, Seats = 4, Travellers = 2, AcceptedCount = (short)acceptedCount,
            DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)), DateTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(22)),
        });
        await s.Db.SaveChangesAsync();
        return (requestId, tripId);
    }

    private static async Task<Guid> AddOfferAsync(JourneyHarness h, Guid requestId, Driver driver, string status, int seq)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequestOffer>().Add(new TaxiRequestOffer
        {
            Id = id, RequestId = requestId, DriverListingId = driver.Listing, DriverUserId = driver.User, Status = status, SeqNo = seq,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ConfirmBy = status == OfferStates.Selected ? DateTime.UtcNow.AddMinutes(15) : null,
        });
        var kind = status == OfferStates.Selected ? DueActionKinds.ConfirmExpire : DueActionKinds.OfferExpire;
        s.Db.Set<DueAction>().Add(new DueAction { Id = Guid.NewGuid(), Kind = kind, SubjectId = id, RequestId = requestId, DueAt = DateTime.UtcNow.AddMinutes(5) });
        if (status == OfferStates.Selected)
            await s.Db.Set<TaxiRequest>().Where(r => r.Id == requestId).ExecuteUpdateAsync(u => u.SetProperty(r => r.SelectedOfferId, id));
        await s.Db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> AddBookingAsync(JourneyHarness h, (Guid RequestId, Guid TripId) request, Guid travellerId, Driver driver, Guid offerId, string status)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiBooking>().Add(new TaxiBooking
        {
            Id = id, RequestId = request.RequestId, OfferId = offerId, TripId = request.TripId, DriverListingId = driver.Listing,
            DriverUserId = driver.User, TravellerUserId = travellerId, Status = status,
            DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)), DateTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(22)),
            BookedAt = DateTime.UtcNow,
        });
        s.Db.Set<DueAction>().Add(new DueAction { Id = Guid.NewGuid(), Kind = DueActionKinds.BookingStart, SubjectId = id, DueAt = DateTime.UtcNow.AddDays(20) });
        await s.Db.SaveChangesAsync();
        return id;
    }

    private async Task<TaxiClosureResult> RunAsync(JourneyHarness h, RecordingPushWriter writer, Func<TaxiRequestClosure, ApplicationDbContext, Task<TaxiClosureResult>> step)
    {
        await using var s = h.NewScope();
        await using var tx = await s.Db.Database.BeginTransactionAsync();
        var result = await step(Closure(s.Db, writer), s.Db);
        await s.Db.SaveChangesAsync();
        await tx.CommitAsync();
        return result;
    }

    private async Task<TaxiRequestOffer> OfferAsync(Guid id)
    {
        await using var read = CreateContext();
        return await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == id);
    }

    private async Task<TaxiRequest> RequestAsync(Guid id)
    {
        await using var read = CreateContext();
        return await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<TaxiBooking> BookingAsync(Guid id)
    {
        await using var read = CreateContext();
        return await read.Set<TaxiBooking>().AsNoTracking().SingleAsync(b => b.Id == id);
    }

    [IntegrationFact]
    public async Task Listing_removed_leaves_no_live_offer_or_active_booking_for_it()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var gone = await AddDriverAsync(h, "Gone");
        var other = await AddDriverAsync(h, "Other");

        var ringingReq = await AddRequestAsync(h, traveller, RequestStates.Open);
        var ringing = await AddOfferAsync(h, ringingReq.RequestId, gone, OfferStates.Ringing, 1);
        var collecting = await AddRequestAsync(h, traveller, RequestStates.Collecting, acceptedCount: 2);
        var accepted = await AddOfferAsync(h, collecting.RequestId, gone, OfferStates.Accepted, 1);
        var otherAccepted = await AddOfferAsync(h, collecting.RequestId, other, OfferStates.Accepted, 2);
        var selectedReq = await AddRequestAsync(h, traveller, RequestStates.Selected, acceptedCount: 1);
        var selected = await AddOfferAsync(h, selectedReq.RequestId, gone, OfferStates.Selected, 1);
        var bookedReq = await AddRequestAsync(h, traveller, RequestStates.Booked, acceptedCount: 1);
        var confirmed = await AddOfferAsync(h, bookedReq.RequestId, gone, OfferStates.Confirmed, 1);
        var booking = await AddBookingAsync(h, bookedReq, traveller, gone, confirmed, BookingStates.Booked);

        var writer = new RecordingPushWriter();
        var result = await RunAsync(h, writer, (c, d) => c.ListingRemovedAsync(d, gone.Listing));

        await using (var read = CreateContext())
        {
            Assert.False(await read.Set<TaxiRequestOffer>().AnyAsync(o => o.DriverListingId == gone.Listing && OfferStates.Live.Contains(o.Status)));
            Assert.False(await read.Set<TaxiBooking>().AnyAsync(b => b.DriverListingId == gone.Listing && BookingStates.Active.Contains(b.Status)));
        }
        Assert.Equal(OfferEndReasons.ListingRemoved, (await OfferAsync(ringing)).EndReason);
        Assert.Equal(OfferStates.Closed, (await OfferAsync(accepted)).Status);
        Assert.Equal(OfferStates.Accepted, (await OfferAsync(otherAccepted)).Status);
        Assert.Equal(RequestStates.Collecting, (await RequestAsync(collecting.RequestId)).Status);
        Assert.Equal(OfferStates.Closed, (await OfferAsync(selected)).Status);
        Assert.Equal(RequestStates.Open, (await RequestAsync(selectedReq.RequestId)).Status);
        Assert.Equal(OfferStates.Withdrawn, (await OfferAsync(confirmed)).Status);
        var cancelled = await BookingAsync(booking);
        Assert.Equal(BookingStates.Cancelled, cancelled.Status);
        Assert.Equal(BookingCancelledBy.System, cancelled.CancelledBy);
        var reopened = await RequestAsync(bookedReq.RequestId);
        Assert.Equal(RequestStates.Open, reopened.Status);
        Assert.Equal(TaxiDispatchRules.DefaultAcceptTarget + TaxiDispatchRules.AcceptBatch, reopened.AcceptTarget);

        Assert.Contains(writer.Notices, n => n.Kind == TaxiNoticeKind.SelectionLapsed && n.RequestId == selectedReq.RequestId && n.UserId == traveller);
        Assert.Contains(writer.Notices, n => n.Kind == TaxiNoticeKind.DriverWithdrew && n.RequestId == bookedReq.RequestId && n.UserId == traveller);
        Assert.Equal(4, result.Changes.Count);
        Assert.DoesNotContain(gone.Listing, result.FreedDriverListingIds);
        await using (var read = CreateContext())
            Assert.False(await read.Set<DueAction>().AnyAsync(d => d.State == DueActionStates.Pending
                && (d.SubjectId == ringing || d.SubjectId == selected || d.SubjectId == booking)));
    }

    [IntegrationFact]
    public async Task Listing_removed_during_the_trip_ends_the_request_as_driver_left()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var gone = await AddDriverAsync(h, "Gone");
        var running = await AddRequestAsync(h, traveller, RequestStates.InProgress, acceptedCount: 1);
        var confirmed = await AddOfferAsync(h, running.RequestId, gone, OfferStates.Confirmed, 1);
        var booking = await AddBookingAsync(h, running, traveller, gone, confirmed, BookingStates.InProgress);

        var writer = new RecordingPushWriter();
        await RunAsync(h, writer, (c, d) => c.ListingRemovedAsync(d, gone.Listing));

        var request = await RequestAsync(running.RequestId);
        Assert.Equal(RequestStates.Cancelled, request.Status);
        Assert.Equal(RequestEndReasons.DriverLeftTrip, request.EndReason);
        Assert.Equal(BookingStates.Cancelled, (await BookingAsync(booking)).Status);
        Assert.Contains(writer.Notices, n => n.Kind == TaxiNoticeKind.DriverWithdrew && n.UserId == traveller);
    }

    [IntegrationFact]
    public async Task Listing_taken_down_closes_live_offers_and_keeps_the_booking()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var down = await AddDriverAsync(h, "Down");
        var other = await AddDriverAsync(h, "Other");

        var ringingReq = await AddRequestAsync(h, traveller, RequestStates.Open);
        var ringing = await AddOfferAsync(h, ringingReq.RequestId, down, OfferStates.Ringing, 1);
        var collecting = await AddRequestAsync(h, traveller, RequestStates.Collecting, acceptedCount: 2);
        var accepted = await AddOfferAsync(h, collecting.RequestId, down, OfferStates.Accepted, 1);
        var otherAccepted = await AddOfferAsync(h, collecting.RequestId, other, OfferStates.Accepted, 2);
        var selectedReq = await AddRequestAsync(h, traveller, RequestStates.Selected, acceptedCount: 1);
        var selected = await AddOfferAsync(h, selectedReq.RequestId, down, OfferStates.Selected, 1);
        var bookedReq = await AddRequestAsync(h, traveller, RequestStates.Booked, acceptedCount: 1);
        var confirmed = await AddOfferAsync(h, bookedReq.RequestId, down, OfferStates.Confirmed, 1);
        var booking = await AddBookingAsync(h, bookedReq, traveller, down, confirmed, BookingStates.Booked);

        var writer = new RecordingPushWriter();
        var result = await RunAsync(h, writer, (c, d) => c.ListingTakenDownAsync(d, down.Listing));

        await using (var read = CreateContext())
            Assert.False(await read.Set<TaxiRequestOffer>().AnyAsync(o => o.DriverListingId == down.Listing && OfferStates.Live.Contains(o.Status)));
        var closedRing = await OfferAsync(ringing);
        Assert.Equal((OfferStates.Closed, OfferEndReasons.ListingRemoved), (closedRing.Status, closedRing.EndReason));
        var end = Assert.Single(writer.RingEnds);
        Assert.Equal((ringing, down.User, OfferEndReasons.ListingRemoved), (end.OfferId, end.DriverUserId, end.Reason));
        Assert.Equal(OfferStates.Closed, (await OfferAsync(accepted)).Status);
        Assert.Equal(OfferStates.Accepted, (await OfferAsync(otherAccepted)).Status);
        Assert.Equal(OfferStates.Closed, (await OfferAsync(selected)).Status);
        Assert.Equal(RequestStates.Open, (await RequestAsync(selectedReq.RequestId)).Status);
        Assert.Contains(writer.Notices, n => n.Kind == TaxiNoticeKind.SelectionLapsed && n.RequestId == selectedReq.RequestId && n.UserId == traveller);

        Assert.Equal(OfferStates.Confirmed, (await OfferAsync(confirmed)).Status);
        Assert.Equal(BookingStates.Booked, (await BookingAsync(booking)).Status);
        Assert.Equal(RequestStates.Booked, (await RequestAsync(bookedReq.RequestId)).Status);
        Assert.DoesNotContain(writer.Notices, n => n.Kind == TaxiNoticeKind.DriverWithdrew);
        Assert.Equal(3, result.Changes.Count);
        Assert.DoesNotContain(result.Changes, c => c.RequestId == bookedReq.RequestId);
        await using (var read = CreateContext())
        {
            Assert.False(await read.Set<DueAction>().AnyAsync(d => d.State == DueActionStates.Pending && (d.SubjectId == ringing || d.SubjectId == selected)));
            Assert.True(await read.Set<DueAction>().AnyAsync(d => d.State == DueActionStates.Pending && d.SubjectId == booking));
        }
    }

    [IntegrationFact]
    public async Task Going_offline_closes_only_ringing_offers()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await AddDriverAsync(h, "Offline");
        var open = await AddRequestAsync(h, traveller, RequestStates.Open);
        var ringing = await AddOfferAsync(h, open.RequestId, driver, OfferStates.Ringing, 1);
        var collecting = await AddRequestAsync(h, traveller, RequestStates.Collecting, acceptedCount: 1);
        var accepted = await AddOfferAsync(h, collecting.RequestId, driver, OfferStates.Accepted, 1);
        var bookedReq = await AddRequestAsync(h, traveller, RequestStates.Booked, acceptedCount: 1);
        var confirmed = await AddOfferAsync(h, bookedReq.RequestId, driver, OfferStates.Confirmed, 1);
        var booking = await AddBookingAsync(h, bookedReq, traveller, driver, confirmed, BookingStates.Booked);

        var writer = new RecordingPushWriter();
        var result = await RunAsync(h, writer, (c, d) => c.DriverOfflineAsync(d, driver.Listing));

        var closed = await OfferAsync(ringing);
        Assert.Equal(OfferStates.Closed, closed.Status);
        Assert.Equal(OfferEndReasons.DriverOffline, closed.EndReason);
        Assert.Equal(OfferStates.Accepted, (await OfferAsync(accepted)).Status);
        Assert.Equal(OfferStates.Confirmed, (await OfferAsync(confirmed)).Status);
        Assert.Equal(BookingStates.Booked, (await BookingAsync(booking)).Status);
        Assert.Equal(RequestStates.Open, (await RequestAsync(open.RequestId)).Status);
        var end = Assert.Single(writer.RingEnds);
        Assert.Equal(OfferEndReasons.DriverOffline, end.Reason);
        Assert.Empty(writer.Notices);
        Assert.Equal([210000001L], result.CityKeys);
        Assert.Empty(result.FreedDriverListingIds);
    }

    [IntegrationFact]
    public async Task Coming_online_changes_no_rows_and_frees_the_driver()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await AddDriverAsync(h, "Online");
        var collecting = await AddRequestAsync(h, traveller, RequestStates.Collecting, acceptedCount: 1);
        var accepted = await AddOfferAsync(h, collecting.RequestId, driver, OfferStates.Accepted, 1);
        var before = await RequestAsync(collecting.RequestId);

        var result = TaxiRequestClosure.DriverOnline(driver.Listing);

        Assert.Equal([driver.Listing], result.FreedDriverListingIds);
        Assert.Equal(before.Version, (await RequestAsync(collecting.RequestId)).Version);
        Assert.Equal(OfferStates.Accepted, (await OfferAsync(accepted)).Status);
    }

    [IntegrationFact]
    public async Task Account_deleted_cancels_every_active_request_and_tells_the_drivers()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var ringDriver = await AddDriverAsync(h, "Ring");
        var bookedDriver = await AddDriverAsync(h, "Booked");
        var open = await AddRequestAsync(h, traveller, RequestStates.Open);
        var ringing = await AddOfferAsync(h, open.RequestId, ringDriver, OfferStates.Ringing, 1);
        var bookedReq = await AddRequestAsync(h, traveller, RequestStates.Booked, acceptedCount: 1);
        var confirmed = await AddOfferAsync(h, bookedReq.RequestId, bookedDriver, OfferStates.Confirmed, 1);
        var booking = await AddBookingAsync(h, bookedReq, traveller, bookedDriver, confirmed, BookingStates.Booked);

        var writer = new RecordingPushWriter();
        var result = await RunAsync(h, writer, (c, d) => c.TravellerAccountDeletedAsync(d, traveller));

        foreach (var id in new[] { open.RequestId, bookedReq.RequestId })
        {
            var request = await RequestAsync(id);
            Assert.Equal(RequestStates.Cancelled, request.Status);
            Assert.Equal(RequestEndReasons.AccountClosed, request.EndReason);
        }
        Assert.Equal(OfferStates.Closed, (await OfferAsync(ringing)).Status);
        var cancelled = await BookingAsync(booking);
        Assert.Equal(BookingStates.Cancelled, cancelled.Status);
        Assert.Equal(BookingCancelledBy.System, cancelled.CancelledBy);
        Assert.Contains(writer.RingEnds, r => r.DriverUserId == ringDriver.User);
        Assert.Contains(writer.Notices, n => n.Kind == TaxiNoticeKind.BookingCancelled && n.UserId == bookedDriver.User);
        Assert.DoesNotContain(writer.Notices, n => n.UserId == traveller);
        Assert.Contains(ringDriver.Listing, result.FreedDriverListingIds);
    }

    [IntegrationFact]
    public async Task Block_closes_offers_between_the_two_and_leaves_the_booking()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var blocked = await AddDriverAsync(h, "Blocked");
        var other = await AddDriverAsync(h, "Other");
        var collecting = await AddRequestAsync(h, traveller, RequestStates.Collecting, acceptedCount: 2);
        var blockedOffer = await AddOfferAsync(h, collecting.RequestId, blocked, OfferStates.Accepted, 1);
        var otherOffer = await AddOfferAsync(h, collecting.RequestId, other, OfferStates.Accepted, 2);
        var bookedReq = await AddRequestAsync(h, traveller, RequestStates.Booked, acceptedCount: 1);
        var confirmed = await AddOfferAsync(h, bookedReq.RequestId, blocked, OfferStates.Confirmed, 1);
        var booking = await AddBookingAsync(h, bookedReq, traveller, blocked, confirmed, BookingStates.Booked);

        var writer = new RecordingPushWriter();
        var result = await RunAsync(h, writer, (c, d) => c.UsersBlockedAsync(d, blocked.User, traveller));

        Assert.Equal(OfferStates.Closed, (await OfferAsync(blockedOffer)).Status);
        Assert.Equal(OfferStates.Accepted, (await OfferAsync(otherOffer)).Status);
        Assert.Equal(RequestStates.Collecting, (await RequestAsync(collecting.RequestId)).Status);
        Assert.Equal(OfferStates.Confirmed, (await OfferAsync(confirmed)).Status);
        Assert.Equal(BookingStates.Booked, (await BookingAsync(booking)).Status);
        Assert.Empty(writer.Notices);
        Assert.Equal([blocked.Listing], result.FreedDriverListingIds);
    }

    [IntegrationFact]
    public async Task Trip_deleted_cancels_only_the_active_requests_of_that_trip()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await AddDriverAsync(h, "Ring");
        var deleted = await AddRequestAsync(h, traveller, RequestStates.Open);
        var ringing = await AddOfferAsync(h, deleted.RequestId, driver, OfferStates.Ringing, 1);
        var kept = await AddRequestAsync(h, traveller, RequestStates.Open);
        var keptOffer = await AddOfferAsync(h, kept.RequestId, driver, OfferStates.Ringing, 1);

        var writer = new RecordingPushWriter();
        var result = await RunAsync(h, writer, (c, d) => c.TripDeletedAsync(d, deleted.TripId));

        var request = await RequestAsync(deleted.RequestId);
        Assert.Equal(RequestStates.Cancelled, request.Status);
        Assert.Equal(RequestEndReasons.TravellerCancelled, request.EndReason);
        Assert.Equal(OfferStates.Closed, (await OfferAsync(ringing)).Status);
        Assert.Equal(RequestStates.Open, (await RequestAsync(kept.RequestId)).Status);
        Assert.Equal(OfferStates.Ringing, (await OfferAsync(keptOffer)).Status);
        Assert.Contains(writer.RingEnds, r => r.OfferId == ringing);
        Assert.DoesNotContain(writer.RingEnds, r => r.OfferId == keptOffer);
        Assert.Contains(result.Changes, c => c.RequestId == deleted.RequestId);
        Assert.DoesNotContain(result.Changes, c => c.RequestId == kept.RequestId);
    }

    // The taxi delete order: closure first, listing row written after it, one commit. Enquiry rows close exactly as before.
    [IntegrationFact]
    public async Task Enquiry_closure_with_the_taxi_closure_closes_both_the_enquiry_and_the_taxi_rows()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var gone = await AddDriverAsync(h, "Gone");
        var request = await AddRequestAsync(h, traveller, RequestStates.Open);
        var ringing = await AddOfferAsync(h, request.RequestId, gone, OfferStates.Ringing, 1);
        var enquiryId = Guid.NewGuid();
        await using (var s = h.NewScope())
        {
            s.Db.TripEnquiries.Add(new TripEnquiry
            {
                Id = enquiryId, TripId = request.TripId, TravellerUserId = traveller, OwnerUserId = gone.User, Kind = TripEnquiryKinds.Taxi,
                ListingId = gone.Listing, ListingName = "Gone", Status = TripEnquiryStatuses.Asked, ExpiresAt = DateTime.UtcNow.AddDays(1),
                CheckIn = h.Start, CheckOut = h.Start.AddDays(1), CityLeg = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await s.Db.SaveChangesAsync();
        }

        var writer = new RecordingPushWriter();
        IReadOnlyList<TripClosurePush> pushes;
        await using (var s = h.NewScope())
        {
            var closure = new TripEnquiryClosure(s.Db, s.Uow, h.Chat, h.Notifier, NullLogger<TripEnquiryClosure>.Instance, Closure(s.Db, writer));
            Assert.True(closure.RunsTaxiClosure);
            var taxi = await s.Db.TaxiListings.SingleAsync(t => t.Id == gone.Listing);
            taxi.IsDeleted = true;
            taxi.DeletedAt = DateTime.UtcNow;
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            pushes = await closure.OnListingsRemovedAsync(ListingKinds.Taxi, [gone.Listing]);
            await s.Db.SaveChangesAsync();
            await tx.CommitAsync();
            await closure.AfterCommitAsync(pushes);
        }

        await using var read = CreateContext();
        var enquiry = await read.TripEnquiries.AsNoTracking().SingleAsync(e => e.Id == enquiryId);
        Assert.Equal(TripEnquiryStatuses.NotAvailable, enquiry.Status);
        Assert.Equal(TripFailReasons.ListingRemoved, enquiry.FailReason);
        Assert.Single(pushes, p => p.EnquiryId == enquiryId);
        var offer = await OfferAsync(ringing);
        Assert.Equal(OfferStates.Closed, offer.Status);
        Assert.Equal(OfferEndReasons.ListingRemoved, offer.EndReason);
        Assert.True((await read.TaxiListings.AsNoTracking().SingleAsync(t => t.Id == gone.Listing)).IsDeleted);
        Assert.Contains(writer.RingEnds, r => r.OfferId == ringing && r.Reason == OfferEndReasons.ListingRemoved);
    }
}
