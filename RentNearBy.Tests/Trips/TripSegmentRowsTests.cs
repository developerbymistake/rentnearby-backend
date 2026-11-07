namespace RentNearBy.Tests.Trips;

public class TripSegmentRowsTests
{
    private static readonly Guid JourneyA = Guid.Parse("dddddddd-0000-0000-0000-00000000000a");
    private static readonly Guid JourneyB = Guid.Parse("dddddddd-0000-0000-0000-00000000000b");

    private static TripPlan Plan(Guid journeyId)
    {
        var plan = new TripPlan
        {
            Legs =
            [
                new TripCityLeg { Index = 1, CityName = "Haridwar" },
                new TripCityLeg
                {
                    Index = 2, CityName = "Badrinath",
                    Arrival = new TripTravel { FromName = "Haridwar", JourneyId = journeyId, Cars = 2 },
                },
            ],
        };
        for (var d = 1; d <= 4; d++) plan.Days.Add(new TripDay { Day = d, Leg = d <= 2 ? 1 : 2 });
        return plan;
    }

    private static TripEnquiry Row(Guid? tripId, short? day, Guid? journeyId = null, int car = 1) => new()
    {
        Id = Guid.NewGuid(),
        TripId = tripId,
        Kind = TripEnquiryKinds.Taxi,
        Status = TripEnquiryStatuses.Asked,
        Day = day,
        ContextJson = journeyId is { } id ? $$"""{"journeyId":"{{id}}","legIndex":{{car}}}""" : """{"city":"Haridwar"}""",
    };

    [Fact]
    public void A_rate_clear_reaches_local_leads_only()
    {
        var trip = Guid.NewGuid();
        var isLocal = TripSegmentRows.LocalTaxi.Compile();
        Assert.True(isLocal(Row(trip, null)));
        Assert.False(isLocal(Row(trip, 3, JourneyA)));
        Assert.False(isLocal(Row(null, null, JourneyA)));
        var stay = Row(trip, null);
        stay.Kind = TripEnquiryKinds.Stay;
        Assert.False(isLocal(stay));
    }

    [Fact]
    public void Only_the_same_journey_and_car_reuses_a_drivers_travel_day_row()
    {
        var trip = Guid.NewGuid();
        var plan = Plan(JourneyA);
        Assert.Equal(TripSegmentRows.RowOwn, TripSegmentRows.Classify(Row(trip, 3, JourneyA, 2), plan, JourneyA, 2));
        Assert.Equal(TripSegmentRows.RowOther, TripSegmentRows.Classify(Row(trip, 3, JourneyA, 1), plan, JourneyA, 2));
        Assert.Equal(TripSegmentRows.RowOther, TripSegmentRows.Classify(Row(trip, 3), plan, JourneyA, 1));
    }

    [Fact]
    public void A_row_of_a_journey_the_plan_no_longer_holds_is_stale()
    {
        var trip = Guid.NewGuid();
        Assert.Equal(TripSegmentRows.RowStale, TripSegmentRows.Classify(Row(trip, 3, JourneyB, 1), Plan(JourneyA), JourneyA, 1));
        Assert.Equal(TripSegmentRows.RowOther, TripSegmentRows.Classify(Row(trip, 3, JourneyB, 1), Plan(JourneyB), JourneyA, 1));
    }
}
