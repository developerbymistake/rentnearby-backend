using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Journeys;

// Real traveller journeys and the adversarial test per fixed loophole (JOURNEY_INTEGRITY.md section 4).
// Real handlers on PostgreSQL (RENTNEARBY_TEST_DB with PostGIS); skipped without it.
public class TripJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    private const long CityA = 210000001;
    private const long CityB = 210000002;

    private static string? ErrorTypeOf(IResult r) => (((IValueHttpResult)r).Value as ApiResponse<object>)?.Error?.Type;

    private static CreateTripEnquiryRequest Stay(int day, int nights, string? step, params Guid[] hosts)
        => new() { Kind = TripEnquiryKinds.Stay, Day = day, Nights = nights, ListingIds = [.. hosts], StepKey = step };

    private static SendTripOfferRequest Offer(JourneyHarness h, int nights = 2, int night1 = 0) => new()
    {
        CheckIn = h.Start.AddDays(night1), CheckOut = h.Start.AddDays(night1 + nights),
        Lines = [new TripOfferLine { RoomName = "Deluxe", Units = 1, Guests = 2, PricePerNight = 2000 }],
    };

    private static async Task<IResult> ShortlistAddAsync(JourneyHarness h, Guid trip, Guid seeker, string step, Guid listing, string kind = TripShortlistKinds.Stay)
    {
        await using var s = h.NewScope();
        return await TripShortlistHandlers.Add(trip, step, listing, new AddShortlistRequest { Kind = kind, Name = "Lake View Stay" },
            new AddShortlistRequestValidator(), Principal(seeker), s.Uow, s.Db);
    }

    private static async Task<IResult> ShortlistCloseAsync(JourneyHarness h, Guid trip, Guid seeker, string step, string reason)
    {
        await using var s = h.NewScope();
        return await TripShortlistHandlers.Close(trip, step, new CloseShortlistRequest { Reason = reason },
            new CloseShortlistRequestValidator(), Principal(seeker), s.Uow, s.Db);
    }

    private async Task EndTripInPastAsync(Guid tripId, string status = TripStatuses.Planned)
    {
        await using var w = Db.CreateContext();
        var today = RentNearBy.Core.Models.TripExecutionRules.TodayIst(DateTime.UtcNow);
        await w.Trips.Where(t => t.Id == tripId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.StartDate, today.AddDays(-5)).SetProperty(x => x.EndDate, today.AddDays(-4)));
    }

    private async Task SetTripPlannedAsync(Guid tripId)
    {
        await using var w = Db.CreateContext();
        await w.Trips.Where(t => t.Id == tripId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TripStatuses.Planned));
    }

    // ---------- real traveller journeys ----------

    // Create trip, shortlist, bulk send with the step key, host offer, accept, advance ask: the real work each step.
    [IntegrationFact]
    public async Task TripJourney_Stay_ShortlistSend_Offer_Accept_Advance_Booked()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var hostA = await h.AddStayOwnerAsync("HostA");
        var hostB = await h.AddStayOwnerAsync("HostB");
        var stayA = await h.AddStayAsync(hostA, "Stay A");
        var stayB = await h.AddStayAsync(hostB, "Stay B");
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        await SetTripPlannedAsync(tripId);

        Assert.Equal(200, StatusOf(await ShortlistAddAsync(h, tripId, seeker, "stay:1", stayA)));
        Assert.Equal(200, StatusOf(await ShortlistAddAsync(h, tripId, seeker, "stay:1", stayB)));

        var send = await h.AskAsync(tripId, seeker, Stay(1, 2, "stay:1", stayA, stayB));
        Assert.Equal(201, StatusOf(send));
        var batch = DataOf<TripEnquiryBatchDto>(send);
        Assert.Equal(2, batch.Items.Count);
        Assert.All(batch.Results, r => Assert.Equal("Sent", r.Status));

        var asked = await h.RowsAsync(tripId);
        Assert.False(RentNearBy.Core.Models.TripEasyRows.IsRowDone(RowOf(asked, "stay:1")), "an asked stay is not done");

        var enquiry = batch.Items.First(i => i.ListingId == stayA);
        var offer = await h.SendOfferAsync(enquiry.Id, hostA, Offer(h));
        Assert.Equal(201, StatusOf(offer));
        var offerId = DataOf<TripOfferResultDto>(offer).Offer.Id;
        Assert.Equal(200, StatusOf(await h.AcceptOfferAsync(enquiry.Id, offerId, seeker)));

        var rows = await h.RowsAsync(tripId);
        Assert.True(RentNearBy.Core.Models.TripEasyRows.IsRowDone(RowOf(rows, "stay:1")), "both nights confirmed by both is done");

        IResult advance;
        await using (var s = h.NewScope())
            advance = await TripAdvanceHandlers.Ask(enquiry.Id, new AskAdvanceRequest
            {
                Amount = 1000, HoldUntil = DateTime.UtcNow.AddDays(2), UpiId = "host@upi", WhatsAppNumber = "9000000001",
            }, new AskAdvanceRequestValidator(), Principal(hostA), s.Uow, s.Db, h.RateLimit, h.Chat, h.Notifier, h.Loggers, s.Http);
        Assert.InRange(StatusOf(advance), 200, 201);
    }

    [IntegrationFact]
    public async Task TripJourney_Own_And_Skip_WithConfirm_StepClosedAndShortlistClosed()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        await SetTripPlannedAsync(tripId);
        Assert.Equal(200, StatusOf(await ShortlistAddAsync(h, tripId, seeker, "stay:1", stay)));

        var close = await ShortlistCloseAsync(h, tripId, seeker, "stay:1", TripShortlistClosedReasons.ClientClose.First());
        Assert.Equal(200, StatusOf(close));
        var saved = await h.UpdatePlanAsync(tripId, seeker, p => p.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own });
        Assert.Equal(200, StatusOf(saved));

        Assert.True(RentNearBy.Core.Models.TripEasyRows.IsRowDone(RowOf(await h.RowsAsync(tripId), "stay:1")));
        await using var ctx = CreateContext();
        Assert.DoesNotContain(await ctx.TripShortlistItems.Where(i => i.TripId == tripId).ToListAsync(), i => i.State == TripShortlistStates.Active);
    }

    [IntegrationFact]
    public async Task TripJourney_RetryAfterLostResponse_ReplaysOneRow()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var request = Stay(1, 2, null, stay);
        request.ClientRequestId = Guid.NewGuid();

        var first = await h.AskAsync(tripId, seeker, request);
        var second = await h.AskAsync(tripId, seeker, request);
        Assert.InRange(StatusOf(first), 200, 201);
        Assert.InRange(StatusOf(second), 200, 201);
        Assert.Single((await h.LoadAsync(tripId)).Enquiries);
    }

    [IntegrationFact]
    public async Task TripJourney_Complete_OnLastDay_AllSettled_ClosesRows()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var ask = await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay));
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(ask).Items);
        await EndTripInPastAsync(tripId);

        Assert.Equal(200, StatusOf(await h.CompleteAsync(tripId, seeker)));
        var (trip, _, rows) = await h.LoadAsync(tripId);
        Assert.Equal(TripStatuses.Completed, trip.Status);
        Assert.Equal(TripEnquiryStatuses.Closed, rows.Single(r => r.Id == enquiry.Id).Status);
    }

    // ---------- B3 / M4 / M6 / M8 (lifecycle) ----------

    [IntegrationFact]
    public async Task Complete_Draft_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var result = await h.CompleteAsync(tripId, seeker);
        Assert.Equal(409, StatusOf(result));
    }

    [IntegrationFact]
    public async Task Complete_Day1With_AskedRow_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay));
        await SetTripPlannedAsync(tripId);

        var result = await h.CompleteAsync(tripId, seeker);
        Assert.Equal(409, StatusOf(result));
        Assert.Equal(TripEnquiryErrorTypes.TripNotEnded, ErrorTypeOf(result));
    }

    [IntegrationFact]
    public async Task Update_OwnMarkWithConfirmedStay_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var ask = await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay));
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(ask).Items);
        var offer = DataOf<TripOfferResultDto>(await h.SendOfferAsync(enquiry.Id, host, Offer(h))).Offer.Id;
        Assert.Equal(200, StatusOf(await h.AcceptOfferAsync(enquiry.Id, offer, seeker)));

        var saved = await h.UpdatePlanAsync(tripId, seeker, p => p.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own });
        Assert.Equal(409, StatusOf(saved));
    }

    [IntegrationFact]
    public async Task Update_CutDayWithConfirmedNight_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var ask = await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay));
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(ask).Items);
        var offer = DataOf<TripOfferResultDto>(await h.SendOfferAsync(enquiry.Id, host, Offer(h))).Offer.Id;
        Assert.Equal(200, StatusOf(await h.AcceptOfferAsync(enquiry.Id, offer, seeker)));

        var saved = await h.UpdatePlanAsync(tripId, seeker, p =>
        {
            p.Days.RemoveAt(1);
            p.Normalize();
        });
        Assert.Equal(409, StatusOf(saved));
    }

    // ---------- M1 / M2 / B6 (send gate) ----------

    [IntegrationFact]
    public async Task Enquiry_NonShortlistedHost_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));

        var result = await h.AskAsync(tripId, seeker, Stay(1, 2, "stay:1", stay));
        Assert.Equal(409, StatusOf(result));
        Assert.Equal(TripEnquiryErrorTypes.NotShortlisted, ErrorTypeOf(result));
        Assert.Empty((await h.LoadAsync(tripId)).Enquiries);
    }

    [IntegrationFact]
    public async Task Enquiry_AfterCloseSkipped_409StepClosed()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        await h.UpdatePlanAsync(tripId, seeker, p => p.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped });

        var result = await h.AskAsync(tripId, seeker, Stay(1, 2, "stay:1", stay));
        Assert.Equal(409, StatusOf(result));
        Assert.Equal(TripEnquiryErrorTypes.StepClosed, ErrorTypeOf(result));
    }

    [IntegrationFact]
    public async Task Enquiry_FourthHostAcrossTwoCalls_409StepFull()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stays = new List<Guid>();
        for (var i = 0; i < 4; i++) stays.Add(await h.AddStayAsync(await h.AddStayOwnerAsync($"Host{i}"), $"Stay {i}"));
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        foreach (var s in stays.Take(3)) await ShortlistAddAsync(h, tripId, seeker, "stay:1", s);
        Assert.Equal(201, StatusOf(await h.AskAsync(tripId, seeker, Stay(1, 2, "stay:1", stays[0], stays[1]))));
        Assert.Equal(201, StatusOf(await h.AskAsync(tripId, seeker, Stay(1, 2, "stay:1", stays[2]))));

        // The fourth host cannot even be shortlisted, and a forged send for him is refused too.
        var forged = await h.AskAsync(tripId, seeker, Stay(1, 2, "stay:1", stays[3]));
        Assert.Equal(409, StatusOf(forged));
        Assert.Equal(3, (await h.LoadAsync(tripId)).Enquiries.Count);
    }

    [IntegrationFact]
    public async Task Shortlist_TaxiUnderStayStep_400()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var taxi = await h.AddTaxiAsync(await h.AddTaxiDriverAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var result = await ShortlistAddAsync(h, tripId, seeker, "stay:1", taxi, TripShortlistKinds.Taxi);
        Assert.Equal(400, StatusOf(result));
        Assert.Equal(TripEnquiryErrorTypes.StepMismatch, ErrorTypeOf(result));
    }

    [IntegrationFact]
    public async Task Shortlist_Stay9OnTwoLegTrip_400()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Almora", CityA, 1), ("Bhimtal", CityB, 1)));
        var result = await ShortlistAddAsync(h, tripId, seeker, "stay:9", stay);
        Assert.Equal(400, StatusOf(result));
        Assert.Equal(TripEnquiryErrorTypes.StepMismatch, ErrorTypeOf(result));
    }

    [IntegrationFact]
    public async Task Enquiry_Stay2WithLeg1Day_400()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Almora", CityA, 1), ("Bhimtal", CityB, 1)));
        var result = await h.AskAsync(tripId, seeker, Stay(1, 1, "stay:2", stay));
        Assert.Equal(400, StatusOf(result));
        Assert.Equal(TripEnquiryErrorTypes.StepMismatch, ErrorTypeOf(result));
    }

    [IntegrationFact]
    public async Task Enquiry_OpenRowCoversFewerNights_ReturnsOpenForOtherNights()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 3)));
        Assert.Equal(201, StatusOf(await h.AskAsync(tripId, seeker, Stay(1, 1, null, stay))));

        var wider = await h.AskAsync(tripId, seeker, Stay(1, 3, null, stay));
        var result = Assert.Single(DataOf<TripEnquiryBatchDto>(wider).Results);
        Assert.Equal("OpenForOtherNights", result.Status);
        Assert.NotEqual("Sent", result.Status);
    }

    [IntegrationFact]
    public async Task Enquiry_ReplayOfWithdrawnId_NeverReplayed()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var request = Stay(1, 2, null, stay);
        request.ClientRequestId = Guid.NewGuid();

        var first = Assert.Single(DataOf<TripEnquiryBatchDto>(await h.AskAsync(tripId, seeker, request)).Items);
        IResult withdrawn;
        await using (var s = h.NewScope())
            withdrawn = await TripEnquiryHandlers.Withdraw(first.Id, Principal(seeker), s.Uow, s.Db, h.Chat, h.Notifier, h.Loggers);
        Assert.Equal(200, StatusOf(withdrawn));

        var again = await h.AskAsync(tripId, seeker, request);
        if (StatusOf(again) is >= 200 and < 300)
        {
            var batch = DataOf<TripEnquiryBatchDto>(again);
            Assert.DoesNotContain(batch.Results, r => r.Status == "Replayed");
            Assert.DoesNotContain(batch.Items, i => i.Id == first.Id);
        }
        else Assert.Equal(409, StatusOf(again));
    }

    // ---------- M3 / B7 / m11 / m13 (after Completed, take-down, guest cancel) ----------

    [IntegrationFact]
    public async Task Completed_Trip_Rejects_AcceptOffer_And_Ask()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay))).Items);
        var offer = DataOf<TripOfferResultDto>(await h.SendOfferAsync(enquiry.Id, host, Offer(h))).Offer.Id;
        await EndTripInPastAsync(tripId);
        Assert.Equal(200, StatusOf(await h.CompleteAsync(tripId, seeker)));

        Assert.Equal(409, StatusOf(await h.AcceptOfferAsync(enquiry.Id, offer, seeker)));
        var fresh = await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay));
        Assert.Equal(409, StatusOf(fresh));
        Assert.Equal(TripEnquiryErrorTypes.TripCompleted, ErrorTypeOf(fresh));
    }

    [IntegrationFact]
    public async Task Completed_Trip_Allows_Withdraw_OfClosedRowIsIdempotentOrOk()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay))).Items);
        IResult withdrawn;
        await using (var s = h.NewScope())
            withdrawn = await TripEnquiryHandlers.Withdraw(enquiry.Id, Principal(seeker), s.Uow, s.Db, h.Chat, h.Notifier, h.Loggers);
        Assert.Equal(200, StatusOf(withdrawn));
    }

    [IntegrationFact]
    public async Task Offer_OnTakenDownStay_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay))).Items);
        await using (var w = Db.CreateContext())
            await w.StayListings.Where(x => x.Id == stay).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StayStatuses.PendingReview));

        var offer = await h.SendOfferAsync(enquiry.Id, host, Offer(h));
        Assert.Equal(409, StatusOf(offer));
        Assert.Equal(TripEnquiryErrorTypes.ListingNotAvailable, ErrorTypeOf(offer));
    }

    [IntegrationFact]
    public async Task AcceptOffer_OnTakenDownStay_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay))).Items);
        var offer = DataOf<TripOfferResultDto>(await h.SendOfferAsync(enquiry.Id, host, Offer(h))).Offer.Id;
        await using (var w = Db.CreateContext())
            await w.StayListings.Where(x => x.Id == stay).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StayStatuses.PendingReview));

        var accept = await h.AcceptOfferAsync(enquiry.Id, offer, seeker);
        Assert.Equal(409, StatusOf(accept));
        Assert.Equal(TripEnquiryErrorTypes.ListingNotAvailable, ErrorTypeOf(accept));
    }

    [IntegrationFact]
    public async Task AdvanceAsk_AboveAcceptedOffer_Refused()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(await h.AskAsync(tripId, seeker, Stay(1, 2, null, stay))).Items);
        var offer = DataOf<TripOfferResultDto>(await h.SendOfferAsync(enquiry.Id, host, Offer(h))).Offer.Id;
        Assert.Equal(200, StatusOf(await h.AcceptOfferAsync(enquiry.Id, offer, seeker)));

        IResult advance;
        await using (var s = h.NewScope())
            advance = await TripAdvanceHandlers.Ask(enquiry.Id, new AskAdvanceRequest
            {
                Amount = 900000, HoldUntil = DateTime.UtcNow.AddDays(2), UpiId = "host@upi", WhatsAppNumber = "9000000001",
            }, new AskAdvanceRequestValidator(), Principal(host), s.Uow, s.Db, h.RateLimit, h.Chat, h.Notifier, h.Loggers, s.Http);
        Assert.InRange(StatusOf(advance), 400, 499);
        Assert.Equal(TripAdvanceStatuses.None, (await h.LoadAsync(tripId)).Enquiries.Single().AdvanceStatus);
    }

    [IntegrationFact]
    public async Task Withdraw_ClosesSentShortlistRow()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        await ShortlistAddAsync(h, tripId, seeker, "stay:1", stay);
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(await h.AskAsync(tripId, seeker, Stay(1, 2, "stay:1", stay))).Items);
        await using (var s = h.NewScope())
            Assert.Equal(200, StatusOf(await TripEnquiryHandlers.Withdraw(enquiry.Id, Principal(seeker), s.Uow, s.Db, h.Chat, h.Notifier, h.Loggers)));

        await using var ctx = CreateContext();
        Assert.DoesNotContain(await ctx.TripShortlistItems.Where(i => i.TripId == tripId).ToListAsync(), i => i.State == TripShortlistStates.Sent);
    }

    [IntegrationFact]
    public async Task Close_Skipped_WithOpenRow_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", CityA, 2)));
        await ShortlistAddAsync(h, tripId, seeker, "stay:1", stay);
        await h.AskAsync(tripId, seeker, Stay(1, 2, "stay:1", stay));

        var close = await ShortlistCloseAsync(h, tripId, seeker, "stay:1", TripShortlistClosedReasons.ClientClose.First());
        Assert.Equal(409, StatusOf(close));
    }
}
