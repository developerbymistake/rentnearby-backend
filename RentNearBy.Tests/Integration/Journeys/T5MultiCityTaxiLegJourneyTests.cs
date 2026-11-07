using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Data;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration.Journeys;

// T5: two cities. The same driver can lead in each city (one open row per driver per city) and a stay ask can cover part of a leg.
public class T5MultiCityTaxiLegJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static CreateTripEnquiryRequest Lead(Guid taxi, params int[] days)
        => new() { Kind = TripEnquiryKinds.Taxi, Days = [.. days], ListingIds = [taxi] };

    [IntegrationFact]
    public async Task SameDriver_LeadsInTwoCities_TwoRows_ThirdLeadMergesIntoItsCity()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var taxi = await h.AddTaxiAsync(driver);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 3), ("Almora", 210000002, 2)));

        var first = await h.AskAsync(tripId, seeker, Lead(taxi, 1, 2));
        Assert.Equal(201, StatusOf(first));
        var firstRow = Assert.Single(DataOf<TripEnquiryBatchDto>(first).Items);
        Assert.Equal(1, firstRow.CityLeg);

        var second = await h.AskAsync(tripId, seeker, Lead(taxi, 4));
        Assert.Equal(201, StatusOf(second));
        var secondRow = Assert.Single(DataOf<TripEnquiryBatchDto>(second).Items);
        Assert.Equal(2, secondRow.CityLeg);
        Assert.NotEqual(firstRow.Id, secondRow.Id);

        var (_, _, enquiries) = await h.LoadAsync(tripId);
        var open = enquiries.Where(e => e.Kind == TripEnquiryKinds.Taxi && TripEnquiryStatuses.Open.Contains(e.Status)).ToList();
        Assert.Equal(2, open.Count);
        Assert.Equal(new int?[] { 1, 2 }, open.Select(e => (int?)e.CityLeg).OrderBy(x => x).ToArray());

        // Day 3 is new for the Nainital row: it takes the day in place, the Almora row stays as it was.
        var third = await h.AskAsync(tripId, seeker, Lead(taxi, 3));
        Assert.Equal(201, StatusOf(third));
        var batch = DataOf<TripEnquiryBatchDto>(third);
        Assert.Equal(firstRow.Id, Assert.Single(batch.Updated));
        Assert.Equal(1, Assert.Single(batch.Items).CityLeg);

        (_, _, enquiries) = await h.LoadAsync(tripId);
        open = enquiries.Where(e => e.Kind == TripEnquiryKinds.Taxi && TripEnquiryStatuses.Open.Contains(e.Status)).ToList();
        Assert.Equal(2, open.Count);

        var taxiRow = RowOf(await h.RowsAsync(tripId), "taxi");
        Assert.Equal(TripRowStates.Asked, taxiRow.State);
        Assert.Equal(2, taxiRow.AskedCount);
    }

    [IntegrationFact]
    public async Task StayAskForTwoOfThreeNights_RowIsPartial_NotDone_ThenLastNightCompletesIt()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 3)));

        var ask = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay],
        });
        Assert.Equal(201, StatusOf(ask));

        var stayRow = RowOf(await h.RowsAsync(tripId), "stay:1");
        Assert.Equal(TripRowStates.Asked, stayRow.State);
        Assert.Equal(3, stayRow.NightsTotal);
        Assert.Equal(2, stayRow.NightsCovered);
        Assert.True(stayRow.Partial);
        Assert.False(TripEasyRows.IsRowDone(stayRow));
        Assert.Equal(0, (await h.ProgressAsync(tripId)).Done);

        var last = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Stay, Day = 3, Nights = 1, ListingIds = [stay],
        });
        Assert.Equal(201, StatusOf(last));

        stayRow = RowOf(await h.RowsAsync(tripId), "stay:1");
        Assert.Equal(3, stayRow.NightsCovered);
        Assert.False(stayRow.Partial);
    }
}
