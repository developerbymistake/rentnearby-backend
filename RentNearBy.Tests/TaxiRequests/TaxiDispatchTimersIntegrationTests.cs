using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

// Written for a real PostgreSQL with PostGIS (RENTNEARBY_TEST_DB); skipped without one. Owner rows O5, O7, O12.
[Trait("Category", "Integration")]
public class TaxiDispatchTimersIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private const long City = 210000001;

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

    private static TaxiRequestStore Store(RecordingPushWriter writer)
    {
        var s = new NullSignals();
        return new TaxiRequestStore(writer, new DueActionScheduler(), s, s, s, s, NullLogger<TaxiRequestStore>.Instance);
    }

    private static TaxiRequestClosure Closure(ApplicationDbContext db, RecordingPushWriter writer)
        => new(Store(writer), writer, new DueActionScheduler(), new TaxiPickupPlaces(db, null!), NullLogger<TaxiRequestClosure>.Instance);

    private sealed record Driver(Guid Listing, Guid User);

    private static async Task<Driver> AddDriverAsync(JourneyHarness h, string name)
    {
        var user = await h.AddTaxiDriverAsync(name);
        return new Driver(await h.AddTaxiAsync(user), user);
    }

    private static async Task<Guid> AddRequestAsync(JourneyHarness h, Guid travellerId, string status, long matchTo = City, int acceptedCount = 0,
        int acceptTarget = TaxiDispatchRules.DefaultAcceptTarget, bool exhausted = false, int daysAhead = 20)
    {
        var tripId = await h.AddTripAsync(travellerId, h.BuildPlan(("Nainital", City, 3)));
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequest>().Add(new TaxiRequest
        {
            Id = id, TripId = tripId, TravellerUserId = travellerId, Status = status, WantSightseeing = true,
            DispatchCityKey = matchTo, MatchToCityKey = matchTo, Seats = 4, Travellers = 2,
            AcceptedCount = (short)acceptedCount, AcceptTarget = (short)acceptTarget,
            QueueExhaustedAt = exhausted ? DateTime.UtcNow : null,
            DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysAhead)), DateTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysAhead + 2)),
        });
        await s.Db.SaveChangesAsync();
        return id;
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
        if (status == OfferStates.Ringing)
            s.Db.Set<DueAction>().Add(new DueAction { Id = Guid.NewGuid(), Kind = DueActionKinds.OfferExpire, SubjectId = id, RequestId = requestId, DueAt = DateTime.UtcNow.AddMinutes(5) });
        if (status == OfferStates.Selected)
            await s.Db.Set<TaxiRequest>().Where(r => r.Id == requestId).ExecuteUpdateAsync(u => u.SetProperty(r => r.SelectedOfferId, id));
        await s.Db.SaveChangesAsync();
        return id;
    }

    private async Task<T> InTxAsync<T>(JourneyHarness h, Func<ApplicationDbContext, Task<T>> step)
    {
        await using var s = h.NewScope();
        await using var tx = await s.Db.Database.BeginTransactionAsync();
        var result = await step(s.Db);
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

    private async Task<List<DueAction>> RemindersAsync(Guid requestId)
    {
        await using var read = CreateContext();
        return await read.Set<DueAction>().AsNoTracking()
            .Where(d => d.RequestId == requestId && DueActionKinds.ChooseReminders.Contains(d.Kind)).ToListAsync();
    }

    // O12
    [IntegrationFact]
    public async Task Route_delete_closes_ringing_and_accepted_offers_only_that_route_served()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await AddDriverAsync(h, "Router");
        var other = await AddDriverAsync(h, "Other");
        const long gone = 210000077, kept = 210000088;

        var ringingReq = await AddRequestAsync(h, traveller, RequestStates.Open, gone);
        var ringing = await AddOfferAsync(h, ringingReq, driver, OfferStates.Ringing, 1);
        var collectingReq = await AddRequestAsync(h, traveller, RequestStates.Collecting, gone, acceptedCount: 2);
        var accepted = await AddOfferAsync(h, collectingReq, driver, OfferStates.Accepted, 1);
        var otherAccepted = await AddOfferAsync(h, collectingReq, other, OfferStates.Accepted, 2);
        var keptReq = await AddRequestAsync(h, traveller, RequestStates.Open, kept);
        var keptRinging = await AddOfferAsync(h, keptReq, driver, OfferStates.Ringing, 1);
        var selectedReq = await AddRequestAsync(h, traveller, RequestStates.Selected, gone, acceptedCount: 1);
        var selected = await AddOfferAsync(h, selectedReq, driver, OfferStates.Selected, 1);

        var writer = new RecordingPushWriter();
        var result = await InTxAsync(h, d => Closure(d, writer).RoutesChangedAsync(d, driver.Listing, [new RouteServeKeys(kept, kept)]));

        var closed = await OfferAsync(ringing);
        Assert.Equal(OfferStates.Closed, closed.Status);
        Assert.Equal(OfferEndReasons.RouteRemoved, closed.EndReason);
        Assert.Equal(OfferStates.Closed, (await OfferAsync(accepted)).Status);
        Assert.Equal(OfferStates.Accepted, (await OfferAsync(otherAccepted)).Status);
        Assert.Equal(RequestStates.Collecting, (await RequestAsync(collectingReq)).Status);
        Assert.Equal(OfferStates.Ringing, (await OfferAsync(keptRinging)).Status);

        var end = Assert.Single(writer.RingEnds);
        Assert.Equal(ringing, end.OfferId);
        Assert.Equal(OfferEndReasons.RouteRemoved, end.Reason);
        Assert.Empty(writer.Notices);
        Assert.Contains(driver.Listing, result.FreedDriverListingIds);
        await using var read = CreateContext();
        Assert.Equal(0, (await read.TaxiListings.AsNoTracking().SingleAsync(l => l.Id == driver.Listing)).RingMissStreak);
        Assert.False(await read.Set<DueAction>().AnyAsync(d => d.SubjectId == ringing && d.State == DueActionStates.Pending));
    }

    [IntegrationFact]
    public async Task Only_a_pending_null_key_keeps_the_offers_of_a_removed_route()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await AddDriverAsync(h, "Unresolved");
        var request = await AddRequestAsync(h, traveller, RequestStates.Open);
        var ringing = await AddOfferAsync(h, request, driver, OfferStates.Ringing, 1);

        var writer = new RecordingPushWriter();
        await InTxAsync(h, d => Closure(d, writer).RoutesChangedAsync(d, driver.Listing, [new RouteServeKeys(null, null, ToPending: true)]));
        Assert.Equal(OfferStates.Ringing, (await OfferAsync(ringing)).Status);

        await InTxAsync(h, d => Closure(d, writer).RoutesChangedAsync(d, driver.Listing, [new RouteServeKeys(null, null)]));
        Assert.Equal(OfferStates.Closed, (await OfferAsync(ringing)).Status);
        Assert.Equal(ringing, Assert.Single(writer.RingEnds).OfferId);
    }

    [IntegrationFact]
    public async Task Route_delete_of_the_last_accept_reopens_the_request()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await AddDriverAsync(h, "Lone");
        var request = await AddRequestAsync(h, traveller, RequestStates.Collecting, acceptedCount: 1);
        var accepted = await AddOfferAsync(h, request, driver, OfferStates.Accepted, 1);

        var writer = new RecordingPushWriter();
        await InTxAsync(h, d => Closure(d, writer).RoutesChangedAsync(d, driver.Listing, []));

        Assert.Equal(OfferStates.Closed, (await OfferAsync(accepted)).Status);
        Assert.Equal(RequestStates.Open, (await RequestAsync(request)).Status);
        Assert.Empty(writer.RingEnds);
    }

    // O5
    [IntegrationFact]
    public async Task Lapsed_selection_keeps_the_exhausted_mark_and_a_withdraw_reopen_clears_it()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var a = await AddDriverAsync(h, "A");
        var b = await AddDriverAsync(h, "B");
        var lapsed = await AddRequestAsync(h, traveller, RequestStates.Selected, acceptedCount: 2, exhausted: true);
        await AddOfferAsync(h, lapsed, a, OfferStates.Accepted, 1);
        await AddOfferAsync(h, lapsed, b, OfferStates.Selected, 2);
        var withdrawn = await AddRequestAsync(h, traveller, RequestStates.Booked, acceptedCount: 1, exhausted: true);

        var writer = new RecordingPushWriter();
        await InTxAsync(h, d => Store(writer).ReopenRequestAsync(d, lapsed, 0));
        await InTxAsync(h, d => Store(writer).ReopenRequestAsync(d, withdrawn, TaxiDispatchRules.AcceptBatch));

        var kept = await RequestAsync(lapsed);
        Assert.Equal(RequestStates.Collecting, kept.Status);
        Assert.NotNull(kept.QueueExhaustedAt);
        Assert.Null((await RequestAsync(withdrawn)).QueueExhaustedAt);
    }

    // Even at the cap of 9 asked, an exhausted request left with no live driver stays Open with ask more; nothing ends it or
    // rings for it until the traveller taps ask more, which clears the mark without lowering the target.
    [IntegrationFact]
    public async Task Exhausted_request_with_no_driver_left_waits_for_ask_more()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await AddDriverAsync(h, "Last");
        var capped = await AddRequestAsync(h, traveller, RequestStates.Selected, acceptedCount: 8, acceptTarget: TaxiDispatchRules.MaxAccepts, exhausted: true);
        // The caller has already moved the selected offer (confirm timeout) before it reopens the request.
        await AddOfferAsync(h, capped, driver, OfferStates.Missed, 1);

        var writer = new RecordingPushWriter();
        var move = await InTxAsync(h, d => Store(writer).ReopenRequestAsync(d, capped, 0));

        Assert.Equal(RequestStates.Open, move!.ToState);
        var open = await RequestAsync(capped);
        Assert.Equal(RequestStates.Open, open.Status);
        Assert.NotNull(open.QueueExhaustedAt);
        Assert.True(TaxiDispatchRules.CanAskMore(open.Status, open.AcceptedCount, open.AcceptTarget, open.QueueExhaustedAt is not null));
        Assert.DoesNotContain(writer.Notices, n => n.Kind == TaxiNoticeKind.RequestEnded);

        await using (var s = h.NewScope())
            Assert.Equal(1, await s.Db.Database.ExecuteSqlRawAsync(TaxiRequestService.AskMoreSql,
                new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = capped }));
        var asked = await RequestAsync(capped);
        Assert.Null(asked.QueueExhaustedAt);
        Assert.Equal(TaxiDispatchRules.MaxAccepts, asked.AcceptTarget);
    }

    // O7
    [IntegrationFact]
    public async Task Reminders_are_armed_sent_with_the_real_count_and_cancelled_on_select()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var a = await AddDriverAsync(h, "A");
        var b = await AddDriverAsync(h, "B");
        var request = await AddRequestAsync(h, traveller, RequestStates.Collecting, acceptedCount: 2);
        await AddOfferAsync(h, request, a, OfferStates.Accepted, 1);
        await AddOfferAsync(h, request, b, OfferStates.Accepted, 2);
        var dateFrom = (await RequestAsync(request)).DateFrom;

        await InTxAsync(h, async d => { await ChooseReminders.ArmAsync(new DueActionScheduler(), d, request, dateFrom, default); return 0; });
        var armed = await RemindersAsync(request);
        Assert.Equal(2, armed.Count);
        var first = armed.Single(r => r.Kind == DueActionKinds.ChooseReminderFirst);
        var second = armed.Single(r => r.Kind == DueActionKinds.ChooseReminderSecond);
        Assert.Equal(first.CreatedAt.AddHours(2), first.DueAt, TimeSpan.FromSeconds(5));
        Assert.Equal(second.CreatedAt.AddHours(24), second.DueAt, TimeSpan.FromSeconds(5));

        var writer = new RecordingPushWriter();
        var item = new DueActionItem(first.Id, first.Kind, request, first.RoundNo, request, first.DueAt, 0);
        var outcome = await InTxAsync(h, d => new ChooseReminderFirstHandler(Store(writer), writer).HandleAsync(d, item, default));
        Assert.Equal(DueOutcome.Done, outcome);
        var notice = Assert.Single(writer.Notices);
        Assert.Equal(TaxiNoticeKind.ChooseDriver, notice.Kind);
        Assert.Equal(traveller, notice.UserId);
        Assert.Equal(2, notice.Count);

        await InTxAsync(h, async d => { await ChooseReminders.CancelAsync(new DueActionScheduler(), d, request, default); return 0; });
        Assert.All(await RemindersAsync(request), r => { Assert.Equal(DueActionStates.Cancelled, r.State); Assert.NotNull(r.DoneAt); });
    }

    [IntegrationFact]
    public async Task Reminder_is_skipped_once_selected_or_ended_and_never_armed_past_the_stale_expiry()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var selected = await AddRequestAsync(h, traveller, RequestStates.Selected, acceptedCount: 1);
        var writer = new RecordingPushWriter();
        var item = new DueActionItem(Guid.NewGuid(), DueActionKinds.ChooseReminderSecond, selected, 1, selected, DateTime.UtcNow, 0);
        Assert.Equal(DueOutcome.Skipped, await InTxAsync(h, d => new ChooseReminderSecondHandler(Store(writer), writer).HandleAsync(d, item, default)));
        Assert.Empty(writer.Notices);

        var tomorrow = await AddRequestAsync(h, traveller, RequestStates.Collecting, acceptedCount: 1, daysAhead: 0);
        var dateFrom = (await RequestAsync(tomorrow)).DateFrom;
        await InTxAsync(h, async d => { await ChooseReminders.ArmAsync(new DueActionScheduler(), d, tomorrow, dateFrom, default); return 0; });
        var stale = TaxiDispatchRules.StaleDueAt(dateFrom);
        Assert.All(await RemindersAsync(tomorrow), r => Assert.True(r.DueAt < stale));
        Assert.DoesNotContain(await RemindersAsync(tomorrow), r => r.Kind == DueActionKinds.ChooseReminderSecond);
    }
}
