using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Data;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration.Journeys;

// B4 on the real database (needs RENTNEARBY_TEST_DB): Rahul drops a city, the draft cap, Mark as booked.
public class B4TripCoreJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    private async Task AddShortlistAsync(Guid tripId, Guid seeker, string stepKey, string state = TripShortlistStates.Active)
    {
        await using var ctx = CreateContext();
        ctx.TripShortlistItems.Add(new TripShortlistItem
        {
            Id = Guid.NewGuid(), TripId = tripId, TravellerUserId = seeker, StepKey = stepKey, Kind = TripShortlistKinds.Stay,
            ListingId = Guid.NewGuid(), ListingName = "Lake View Stay", AddedAt = DateTime.UtcNow,
            ClosesAt = DateTime.UtcNow.AddDays(3), State = state,
        });
        await ctx.SaveChangesAsync();
    }

    [IntegrationFact]
    public async Task SaveTrip_closes_active_shortlist_rows_of_a_removed_city_and_keeps_the_rest()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Almora", 210000001, 2), ("Bhimtal", 210000002, 2)));
        await AddShortlistAsync(tripId, seeker, "stay:1");
        await AddShortlistAsync(tripId, seeker, "stay:2");
        await AddShortlistAsync(tripId, seeker, "between:2");
        await AddShortlistAsync(tripId, seeker, "stay:2", TripShortlistStates.Sent);

        var saved = await h.UpdatePlanAsync(tripId, seeker, plan =>
        {
            plan.Legs!.RemoveAt(1);
            foreach (var day in plan.Days) day.Leg = 1;
        });
        Assert.Equal(200, StatusOf(saved));

        await using var ctx = CreateContext();
        var rows = await ctx.TripShortlistItems.Where(i => i.TripId == tripId).ToListAsync();
        Assert.Equal(TripShortlistStates.Active, rows.Single(r => r.StepKey == "stay:1").State);
        var gone = rows.Where(r => r.StepKey is "between:2" || (r.StepKey == "stay:2" && r.State != TripShortlistStates.Sent)).ToList();
        Assert.Equal(2, gone.Count);
        Assert.All(gone, r =>
        {
            Assert.Equal(TripShortlistStates.Closed, r.State);
            Assert.Equal("LegRemoved", r.ClosedReason);
            Assert.NotNull(r.ClosedAt);
        });
        Assert.Equal(TripShortlistStates.Sent, rows.Single(r => r.StepKey == "stay:2" && r.State == TripShortlistStates.Sent).State);
    }

    [IntegrationFact]
    public async Task Create_refuses_the_31st_open_draft_with_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        for (var i = 0; i < TripCreateRules.MaxOpenDrafts; i++)
            await h.AddTripAsync(seeker, h.BuildPlan(("Almora", 210000001, 1)));

        await using var s = h.NewScope();
        var result = await TripHandlers.Create(new SaveTripRequest { Plan = h.BuildPlan(("Almora", 210000001, 1)) },
            new SaveTripRequestValidator(), Principal(seeker), s.Uow, null!, s.Journeys, h.Loggers, h.RateLimit,
            new DefaultHttpContext(), s.Db);
        Assert.Equal(409, StatusOf(result));
    }

    [IntegrationFact]
    public async Task MarkBooked_leaves_no_pending_offer_and_closes_the_enquiry()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var ask = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay], StepKey = "stay:1",
        });
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(ask).Items);
        var offer = await h.SendOfferAsync(enquiry.Id, host, new SendTripOfferRequest
        {
            CheckIn = h.Start, CheckOut = h.Start.AddDays(2),
            Lines = [new TripOfferLine { RoomName = "Deluxe", Units = 1, Guests = 2, PricePerNight = 2000 }],
        });
        Assert.Equal(201, StatusOf(offer));

        IResult booked;
        await using (var s = h.NewScope())
            booked = await TripShortlistHandlers.MarkBooked(tripId, "stay:1", new MarkBookedRequest(), new MarkBookedRequestValidator(),
                Principal(seeker), s.Uow, s.Db, h.Chat, h.Loggers);
        Assert.Equal(200, StatusOf(booked));

        await using var ctx = CreateContext();
        Assert.False(await ctx.TripOffers.AnyAsync(o => o.TripEnquiryId == enquiry.Id && o.Status == TripOfferStatuses.Pending));
        Assert.Equal(TripEnquiryStatuses.Closed, (await ctx.TripEnquiries.FirstAsync(e => e.Id == enquiry.Id)).Status);
    }
}
