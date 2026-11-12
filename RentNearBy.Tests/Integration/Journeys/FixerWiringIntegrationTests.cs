using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Tests.Data;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration.Journeys;

// Wiring of the journey gates (B7, M1, M3, race of Complete): DB-backed, skipped without PostgreSQL.
public class FixerWiringIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static string? ErrorTypeOf(IResult r) => (((IValueHttpResult)r).Value as ApiResponse<object>)?.Error?.Type;

    private async Task<(Guid Enquiry, Guid Offer)> AskAndOfferAsync(JourneyHarness h, Guid tripId, Guid seeker, Guid host, Guid stay)
    {
        var ask = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest { Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay] });
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(ask).Items);
        var offer = await h.SendOfferAsync(enquiry.Id, host, new SendTripOfferRequest
        {
            CheckIn = h.Start, CheckOut = h.Start.AddDays(2),
            Lines = [new TripOfferLine { RoomName = "Deluxe", Units = 1, Guests = 2, PricePerNight = 2000 }],
        });
        return (enquiry.Id, DataOf<TripOfferResultDto>(offer).Offer.Id);
    }

    [IntegrationFact]
    public async Task AcceptOffer_OnNonApprovedStay_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var (enquiry, offer) = await AskAndOfferAsync(h, tripId, seeker, host, stay);

        await using (var w = Db.CreateContext())
            await w.StayListings.Where(s => s.Id == stay).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StayStatuses.PendingReview));

        var accept = await h.AcceptOfferAsync(enquiry, offer, seeker);
        Assert.Equal(409, StatusOf(accept));
        Assert.Equal(TripEnquiryErrorTypes.ListingNotAvailable, ErrorTypeOf(accept));
    }

    [IntegrationFact]
    public async Task AcceptOffer_OnApprovedStay_Works()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var (enquiry, offer) = await AskAndOfferAsync(h, tripId, seeker, host, stay);
        Assert.Equal(200, StatusOf(await h.AcceptOfferAsync(enquiry, offer, seeker)));
    }

    [IntegrationFact]
    public async Task Enquiry_NoStepKey_OnOwnStep_409StepClosed()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var plan = h.BuildPlan(("Nainital", 210000001, 2));
        plan.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        var tripId = await h.AddTripAsync(seeker, plan);

        var ask = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest { Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay] });
        Assert.Equal(409, StatusOf(ask));
        Assert.Equal(TripEnquiryErrorTypes.StepClosed, ErrorTypeOf(ask));
    }

    [IntegrationFact]
    public async Task Enquiry_NoStepKey_OnOpenStep_StillWorks()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var ask = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest { Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay] });
        Assert.Equal(201, StatusOf(ask));
    }

    [IntegrationFact]
    public async Task TaxiBooking_OnNonApprovedTaxi_409_ApprovedWorks()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var taxi = await h.AddTaxiAsync(await h.AddTaxiDriverAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var request = new CreateTaxiBookingRequest { TaxiId = taxi, Days = [1], AgreedFare = 3000, PickupTime = "08:30" };

        await using (var w = Db.CreateContext())
            await w.TaxiListings.Where(t => t.Id == taxi).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TaxiStatuses.PendingReview));
        var refused = await h.TaxiBookAsync(tripId, seeker, request);
        Assert.Equal(409, StatusOf(refused));
        Assert.Equal(TripEnquiryErrorTypes.ListingNotAvailable, ErrorTypeOf(refused));
        Assert.Empty((await h.LoadAsync(tripId)).Enquiries);

        await using (var w = Db.CreateContext())
            await w.TaxiListings.Where(t => t.Id == taxi).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TaxiStatuses.Approved));
        Assert.InRange(StatusOf(await h.TaxiBookAsync(tripId, seeker, request)), 200, 201);
    }

    // The trip ends in the past after the offer was made, so Complete is allowed and closes the open offer.
    [IntegrationFact]
    public async Task Complete_ClosesOpenRows_ThenAcceptOffer_409TripCompleted_AndShortlistClose_409()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var (enquiry, offer) = await AskAndOfferAsync(h, tripId, seeker, host, stay);

        await using (var w = Db.CreateContext())
        {
            var today = RentNearBy.Core.Models.TripExecutionRules.TodayIst(DateTime.UtcNow);
            await w.Trips.Where(t => t.Id == tripId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TripStatuses.Planned)
                .SetProperty(x => x.StartDate, today.AddDays(-5)).SetProperty(x => x.EndDate, today.AddDays(-4)));
        }
        Assert.Equal(200, StatusOf(await h.CompleteAsync(tripId, seeker)));
        var row = Assert.Single((await h.LoadAsync(tripId)).Enquiries);
        Assert.Equal(TripEnquiryStatuses.Closed, row.Status);

        var accept = await h.AcceptOfferAsync(enquiry, offer, seeker);
        Assert.Equal(409, StatusOf(accept));

        await using var s = h.NewScope();
        var close = await TripShortlistHandlers.Close(tripId, "stay:1", new CloseShortlistRequest { Reason = TripShortlistClosedReasons.ClientClose.First() },
            new CloseShortlistRequestValidator(), Principal(seeker), s.Uow, s.Db);
        Assert.Equal(409, StatusOf(close));
        Assert.Equal(TripEnquiryErrorTypes.TripCompleted, ErrorTypeOf(close));
    }
}
