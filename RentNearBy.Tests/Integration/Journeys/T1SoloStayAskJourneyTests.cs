using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Data;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration.Journeys;

// T1: a solo traveller asks one stay, the host offers, the traveller accepts; the other rows are his own marks.
public class T1SoloStayAskJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task StayAsk_Offer_Accept_RowsMoveAskedOfferConfirmed()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));

        var ask = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay],
        });
        Assert.Equal(201, StatusOf(ask));
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(ask).Items);

        var rows = await h.RowsAsync(tripId);
        var stayRow = RowOf(rows, "stay:1");
        Assert.Equal(TripRowStates.Asked, stayRow.State);
        Assert.Equal(1, stayRow.AskedCount);
        Assert.False(stayRow.NeedsYou);
        Assert.False(stayRow.Partial);
        Assert.Equal(TripStatuses.Planned, (await h.LoadAsync(tripId)).Trip.Status);

        var offer = await h.SendOfferAsync(enquiry.Id, host, new SendTripOfferRequest
        {
            CheckIn = h.Start,
            CheckOut = h.Start.AddDays(2),
            Lines = [new TripOfferLine { RoomName = "Deluxe", Units = 1, Guests = 2, PricePerNight = 2000 }],
        });
        Assert.Equal(201, StatusOf(offer));
        var offerId = DataOf<TripOfferResultDto>(offer).Offer.Id;

        stayRow = RowOf(await h.RowsAsync(tripId), "stay:1");
        Assert.Equal(TripRowStates.Offer, stayRow.State);
        Assert.True(stayRow.NeedsYou);

        var accept = await h.AcceptOfferAsync(enquiry.Id, offerId, seeker);
        Assert.Equal(200, StatusOf(accept));

        var (_, _, enquiries) = await h.LoadAsync(tripId);
        Assert.Equal(TripEnquiryStatuses.ConfirmedByBoth, Assert.Single(enquiries).Status);
        stayRow = RowOf(await h.RowsAsync(tripId), "stay:1");
        Assert.Equal(TripRowStates.Confirmed, stayRow.State);
        Assert.False(stayRow.NeedsYou);
        Assert.True(TripEasyRows.IsRowDone(stayRow));

        // The other rows are his own word: getting there and the taxi are his, coming back is skipped.
        var marked = await h.UpdatePlanAsync(tripId, seeker, plan =>
        {
            plan.Steps ??= new TripSteps();
            plan.Steps.GettingThere = new TripStep { Status = TripStepStatuses.Own };
            plan.Steps.ComingBack = new TripStep { Status = TripStepStatuses.Skipped };
            plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        });
        Assert.Equal(200, StatusOf(marked));

        var after = await h.RowsAsync(tripId);
        Assert.Equal(TripBookingStates.Ready, TripBookingStates.OfRows(after));

        var summary = DataOf<TripSummaryRecordDto>(await h.SummaryAsync(tripId, seeker));
        var arrangement = Assert.Single(summary.Arrangements, a => a.Key == "stay:1");
        Assert.Equal(TripRowStates.Confirmed, arrangement.State);
        Assert.DoesNotContain(summary.Arrangements, a => a.Key == "comingBack");
    }
}
