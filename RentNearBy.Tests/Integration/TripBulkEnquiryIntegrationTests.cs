using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration.Journeys;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration;

// Written for a real PostgreSQL (Category=Integration): skipped without one. Bulk enquiry, idempotent replay, shortlist cap and mark-booked.
[Trait("Category", "Integration")]
public class TripBulkEnquiryIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static async Task<IResult> PutAsync(JourneyHarness h, Guid tripId, Guid caller, string step, Guid listing, string name = "Hill View")
    {
        await using var s = h.NewScope();
        return await TripShortlistHandlers.Add(tripId, step, listing, new AddShortlistRequest { Kind = "Stay", Name = name },
            new AddShortlistRequestValidator(), Principal(caller), s.Uow, s.Db);
    }

    [IntegrationFact]
    public async Task Bulk_send_of_three_writes_rows_inbox_and_outbox_in_one_go_and_replays_on_retry()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stays = new List<Guid>();
        for (var i = 0; i < 3; i++) stays.Add(await h.AddStayAsync(await h.AddStayOwnerAsync($"Host{i}"), $"Stay {i}"));
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var key = Guid.NewGuid();
        var request = new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = stays, ClientRequestId = key, StepKey = "stay:1",
        };

        var first = await h.AskAsync(tripId, seeker, request);
        Assert.Equal(201, StatusOf(first));
        var batch = DataOf<TripEnquiryBatchDto>(first);
        Assert.Equal(stays, batch.Results.Select(r => r.ListingId));
        Assert.All(batch.Results, r => Assert.Equal(TripSendStatuses.Sent, r.Status));

        await using (var read = Db.CreateContext())
        {
            var rows = await read.TripEnquiries.AsNoTracking().Where(x => x.TripId == tripId).ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => { Assert.Equal(key, r.ClientRequestId); Assert.Equal("stay:1", r.StepKey); });
            var hosts = rows.Select(r => r.OwnerUserId!.Value).ToList();
            Assert.Equal(3, await read.NotificationEvents.CountAsync(n => n.TargetUserId != null && hosts.Contains(n.TargetUserId.Value)));
            var keys = rows.Select(r => $"enq:{r.Id:N}:new").ToList();
            Assert.Equal(3, await read.OutboxMessages.CountAsync(o => keys.Contains(o.Key) && o.PublishedAt == null));
        }

        var retry = await h.AskAsync(tripId, seeker, request);
        Assert.Equal(200, StatusOf(retry));
        Assert.All(DataOf<TripEnquiryBatchDto>(retry).Results, r => Assert.Equal(TripSendStatuses.Replayed, r.Status));
        await using var after = Db.CreateContext();
        Assert.Equal(3, await after.TripEnquiries.CountAsync(x => x.TripId == tripId));
    }

    [IntegrationFact]
    public async Task Shortlist_holds_three_per_step_is_idempotent_and_marks_sent_on_send()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stays = new List<Guid>();
        for (var i = 0; i < 4; i++) stays.Add(await h.AddStayAsync(await h.AddStayOwnerAsync($"Host{i}"), $"Stay {i}"));
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));

        for (var i = 0; i < 3; i++) Assert.Equal(200, StatusOf(await PutAsync(h, tripId, seeker, "stay:1", stays[i])));
        Assert.Equal(200, StatusOf(await PutAsync(h, tripId, seeker, "stay:1", stays[0])));
        Assert.Equal(409, StatusOf(await PutAsync(h, tripId, seeker, "stay:1", stays[3])));
        Assert.Equal(200, StatusOf(await PutAsync(h, tripId, seeker, "stay:2", stays[3])));

        var send = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = stays.Take(3).ToList(), StepKey = "stay:1",
        });
        Assert.Equal(201, StatusOf(send));
        await using var read = Db.CreateContext();
        var items = await read.TripShortlistItems.AsNoTracking().Where(x => x.TripId == tripId && x.StepKey == "stay:1").ToListAsync();
        Assert.All(items, i => Assert.Equal(TripShortlistStates.Sent, i.State));
    }

    [IntegrationFact]
    public async Task Mark_booked_closes_open_enquiries_and_the_shortlist_of_the_step()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        await PutAsync(h, tripId, seeker, "stay:1", stay);
        await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay], StepKey = "stay:1",
        });

        await using var s = h.NewScope();
        var result = await TripShortlistHandlers.MarkBooked(tripId, "stay:1", new MarkBookedRequest(), new MarkBookedRequestValidator(),
            Principal(seeker), s.Uow, s.Db, h.Chat, h.Loggers);
        Assert.Equal(200, StatusOf(result));
        var counts = DataOf<MarkBookedResultDto>(result);
        Assert.Equal(1, counts.ClosedEnquiries);
        Assert.Equal(1, counts.ClosedShortlist);

        await using var again = h.NewScope();
        var second = await TripShortlistHandlers.MarkBooked(tripId, "stay:1", new MarkBookedRequest(), new MarkBookedRequestValidator(),
            Principal(seeker), again.Uow, again.Db, h.Chat, h.Loggers);
        Assert.Equal(0, DataOf<MarkBookedResultDto>(second).ClosedEnquiries);

        await using var read = Db.CreateContext();
        var row = await read.TripEnquiries.AsNoTracking().SingleAsync(x => x.TripId == tripId);
        Assert.Equal(TripEnquiryStatuses.Closed, row.Status);
        Assert.Equal(TripCloseReasons.BookedElsewhere, row.FailReason);
        Assert.Contains(TripSystemLines.GuestBookedElsewhere, h.Chat.Posted);
    }

    [IntegrationFact]
    public async Task Same_host_asked_in_another_trip_is_open_in_another_trip_and_writes_nothing_for_this_trip()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripA = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var tripB = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var request = new CreateTripEnquiryRequest { Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay], StepKey = "stay:1" };

        Assert.Equal(201, StatusOf(await h.AskAsync(tripA, seeker, request)));
        var second = await h.AskAsync(tripB, seeker, new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay], StepKey = "stay:1",
        });
        var result = Assert.Single(DataOf<TripEnquiryBatchDto>(second).Results);
        Assert.Equal(TripSendStatuses.OpenInAnotherTrip, result.Status);
        Assert.Null(result.EnquiryId);
        await using var read = Db.CreateContext();
        Assert.Equal(0, await read.TripEnquiries.CountAsync(x => x.TripId == tripB));
    }

    [IntegrationFact]
    public async Task A_deleted_listing_closes_its_shortlist_row_as_unavailable_and_the_other_two_are_sent()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stays = new List<Guid>();
        for (var i = 0; i < 3; i++) stays.Add(await h.AddStayAsync(await h.AddStayOwnerAsync($"Host{i}"), $"Stay {i}"));
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        foreach (var s in stays) Assert.Equal(200, StatusOf(await PutAsync(h, tripId, seeker, "stay:1", s)));
        await using (var del = Db.CreateContext())
            await del.StayListings.Where(x => x.Id == stays[2]).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDeleted, true));

        var send = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = stays, StepKey = "stay:1",
        });
        var results = DataOf<TripEnquiryBatchDto>(send).Results;
        Assert.Equal(2, results.Count(r => r.Status == TripSendStatuses.Sent));
        Assert.Equal(TripSendStatuses.NotAvailable, results.Single(r => r.ListingId == stays[2]).Status);

        await using var read = Db.CreateContext();
        var row = await read.TripShortlistItems.AsNoTracking().SingleAsync(x => x.TripId == tripId && x.ListingId == stays[2]);
        Assert.Equal(TripShortlistStates.Closed, row.State);
        Assert.Equal(TripShortlistClosedReasons.Unavailable, row.ClosedReason);
    }

    [IntegrationFact]
    public async Task Close_then_mark_booked_leaves_no_final_push_row_for_the_step()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        await using (var seed = Db.CreateContext())
        {
            seed.TripShortlistItems.Add(new TripShortlistItem
            {
                Id = Guid.NewGuid(), TripId = tripId, TravellerUserId = seeker, StepKey = "stay:1", Kind = TripShortlistKinds.Stay,
                ListingId = stay, ListingName = "Hill View", AddedAt = DateTime.UtcNow.AddDays(-4), ClosesAt = DateTime.UtcNow.AddDays(-1),
                State = TripShortlistStates.Closed, ClosedReason = TripShortlistClosedReasons.NoReply, ClosedAt = DateTime.UtcNow.AddDays(-1),
            });
            await seed.SaveChangesAsync();
        }

        await using var s = h.NewScope();
        var result = await TripShortlistHandlers.MarkBooked(tripId, "stay:1", new MarkBookedRequest(), new MarkBookedRequestValidator(),
            Principal(seeker), s.Uow, s.Db, h.Chat, h.Loggers);
        Assert.Equal(200, StatusOf(result));

        await using var read = Db.CreateContext();
        Assert.Equal(0, await read.TripShortlistItems.CountAsync(x => x.TripId == tripId && x.State == TripShortlistStates.Closed
            && x.ClosedReason == TripShortlistClosedReasons.NoReply && x.FinalPushedAt == null));
    }
}
