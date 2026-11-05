namespace RentNearBy.Tests.Trips;

// CONTRACT_MCD D1, D2, D4: counters, travel sentence, per-leg state and taxi grouping.
public class TripSummaryLegsTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly Guid TripId = Guid.Parse("cccccccc-0000-0000-0000-00000000000d");

    private static TripPlan Plan(params int[] legOfDay)
    {
        var legCount = legOfDay.Length == 0 ? 1 : legOfDay.Max();
        var names = new[] { "Haridwar", "Badrinath", "Joshimath", "Auli" };
        var plan = new TripPlan
        {
            CityKey = 210000001,
            CityName = names[0],
            Settings = new TripSettings { StartDate = Start },
            Legs = Enumerable.Range(1, legCount)
                .Select(i => new TripCityLeg { Index = i, CityKey = 210000000 + i, CityName = names[i - 1], Latitude = 30, Longitude = 78 })
                .ToList(),
        };
        for (var d = 1; d <= legOfDay.Length; d++)
            plan.Days.Add(new TripDay { Day = d, Leg = legOfDay[d - 1] });
        return plan;
    }

    private static TripTravel Journey(Guid journeyId, int cars = 2) => new()
    {
        Mode = TripTravelModes.Journey,
        FromName = "Haridwar",
        JourneyId = journeyId,
        Cars = cars,
    };

    private static TripTravel Own() => new() { Mode = TripTravelModes.Own, FromName = "Haridwar", How = "bus", LeavesAt = "06:00" };

    private static TripEnquiry Car(Guid journeyId, int car, int day, string status) => new()
    {
        Id = Guid.NewGuid(),
        TripId = TripId,
        Kind = TripEnquiryKinds.Taxi,
        Day = (short)day,
        DaysJson = $"[{day}]",
        CheckIn = Start.AddDays(day - 1),
        CheckOut = Start.AddDays(day),
        Status = status,
        ContextJson = $$"""{"city":"Badrinath","journeyId":"{{journeyId}}","legIndex":{{car}}}""",
    };

    private static TripEnquiry Stay(int night, string status) => new()
    {
        Id = Guid.NewGuid(),
        TripId = TripId,
        Kind = TripEnquiryKinds.Stay,
        Day = (short)night,
        CheckIn = Start.AddDays(night - 1),
        CheckOut = Start.AddDays(night),
        Status = status,
    };

    [Fact]
    public void One_leg_has_no_segments()
        => Assert.Equal((0, 0), TripSummaryLegs.Counters(Plan(1, 1, 1), []));

    [Fact]
    public void Two_legs_without_travel_count_one_open_segment()
        => Assert.Equal((1, 0), TripSummaryLegs.Counters(Plan(1, 2, 2, 2), []));

    [Fact]
    public void Own_travel_is_settled()
    {
        var plan = Plan(1, 2, 2, 2);
        plan.Legs![1].Arrival = Own();
        Assert.Equal((1, 1), TripSummaryLegs.Counters(plan, []));
    }

    [Fact]
    public void Journey_counts_its_cars()
    {
        var plan = Plan(1, 2, 2, 2);
        var journeyId = Guid.NewGuid();
        plan.Legs![1].Arrival = Journey(journeyId);

        Assert.Equal((2, 0), TripSummaryLegs.Counters(plan, []));
        Assert.Equal((2, 1), TripSummaryLegs.Counters(plan, [Car(journeyId, 1, 2, TripEnquiryStatuses.BookedByTraveller)]));
        Assert.Equal((2, 2), TripSummaryLegs.Counters(plan,
        [
            Car(journeyId, 1, 2, TripEnquiryStatuses.BookedByTraveller),
            Car(journeyId, 2, 2, TripEnquiryStatuses.ConfirmedByBoth),
        ]));
        Assert.Equal((2, 0), TripSummaryLegs.Counters(plan, [Car(journeyId, 1, 2, TripEnquiryStatuses.FareSent)]));
    }

    [Fact]
    public void Cars_own_and_unplanned_travel_add_up()
    {
        var plan = Plan(1, 2, 3, 4);
        var journeyId = Guid.NewGuid();
        plan.Legs![1].Arrival = Journey(journeyId);
        plan.Legs![2].Arrival = Own();
        Assert.Equal((4, 2), TripSummaryLegs.Counters(plan, [Car(journeyId, 1, 2, TripEnquiryStatuses.ConfirmedByBoth)]));
    }

    [Fact]
    public void Rows_of_a_replaced_journey_are_ignored()
    {
        var plan = Plan(1, 2, 2, 2);
        var old = Guid.NewGuid();
        plan.Legs![1].Arrival = Journey(Guid.NewGuid());
        Assert.Equal((2, 0), TripSummaryLegs.Counters(plan,
        [
            Car(old, 1, 2, TripEnquiryStatuses.ConfirmedByBoth),
            Car(old, 2, 2, TripEnquiryStatuses.ConfirmedByBoth),
        ]));
    }

    [Fact]
    public void Travel_sentence_is_null_on_one_leg_and_without_travel()
    {
        Assert.Null(TripSummaryLegs.TravelSentence(Plan(1, 1), []));
        Assert.Null(TripSummaryLegs.TravelSentence(Plan(1, 2, 2), []));
    }

    [Fact]
    public void Travel_sentence_texts()
    {
        var own = Plan(1, 2, 2);
        own.Legs![1].Arrival = Own();
        Assert.Equal("Travel booked.", TripSummaryLegs.TravelSentence(own, []));

        var journeyId = Guid.NewGuid();
        var unbooked = Plan(1, 2, 2);
        unbooked.Legs![1].Arrival = Journey(journeyId, cars: 1);
        Assert.Equal("1 travel not booked.", TripSummaryLegs.TravelSentence(unbooked, []));
        Assert.Equal("Travel booked.",
            TripSummaryLegs.TravelSentence(unbooked, [Car(journeyId, 1, 2, TripEnquiryStatuses.BookedByTraveller)]));

        var mixed = Plan(1, 2, 3, 3);
        mixed.Legs![1].Arrival = Journey(Guid.NewGuid());
        mixed.Legs![2].Arrival = Own();
        Assert.Equal("1 travel not booked.", TripSummaryLegs.TravelSentence(mixed, []));

        var two = Plan(1, 2, 3, 3);
        two.Legs![1].Arrival = Journey(Guid.NewGuid());
        two.Legs![2].Arrival = Journey(Guid.NewGuid());
        Assert.Equal("2 travel not booked.", TripSummaryLegs.TravelSentence(two, []));
    }

    [Fact]
    public void Leg_with_nothing_is_draft()
        => Assert.Equal(TripBookingStates.Draft, TripSummaryLegs.StateOf(Plan(1, 2, 2), [], 2));

    [Fact]
    public void Leg_with_a_booked_stay_and_nothing_open_is_ready()
        => Assert.Equal(TripBookingStates.Ready,
            TripSummaryLegs.StateOf(Plan(1, 2, 2), [Stay(1, TripEnquiryStatuses.ConfirmedByBoth)], 1));

    [Fact]
    public void Leg_with_an_open_stay_is_confirming()
    {
        var rows = new[] { Stay(1, TripEnquiryStatuses.Asked) };
        Assert.Equal(TripBookingStates.Confirming, TripSummaryLegs.StateOf(Plan(1, 2, 2), rows, 1));
        Assert.Equal(TripBookingStates.Draft, TripSummaryLegs.StateOf(Plan(1, 2, 2), rows, 2));
    }

    [Fact]
    public void Leg_with_a_saved_unbooked_journey_is_confirming()
    {
        var plan = Plan(1, 2, 2);
        plan.Legs![1].Arrival = Journey(Guid.NewGuid());
        Assert.Equal(TripBookingStates.Confirming, TripSummaryLegs.StateOf(plan, [], 2));
    }

    [Fact]
    public void Local_taxi_inside_one_leg_belongs_to_that_leg()
    {
        var plan = Plan(1, 2, 2, 2);
        Assert.Equal(new List<int> { 2 }, TripSummaryLegs.LegOfTaxi(plan, Start, [3, 4], Start.AddDays(2)));
        Assert.Equal(new List<int> { 1, 2 }, TripSummaryLegs.LegOfTaxi(plan, null, [1, 2], null));
        Assert.Equal(new List<int> { 2 }, TripSummaryLegs.LegOfTaxi(plan, Start, [], Start.AddDays(1)));
    }

    [Fact]
    public void Night_belongs_to_the_leg_of_its_day()
    {
        var plan = Plan(1, 2, 2, 2);
        Assert.Equal(1, TripSummaryLegs.LegOfNight(plan, 1));
        Assert.Equal(2, TripSummaryLegs.LegOfNight(plan, 4));
        Assert.Equal(new HashSet<int> { 2, 3, 4 }, TripSummaryLegs.LegDays(plan, 2));
    }
}
