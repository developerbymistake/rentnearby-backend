using System.Text.Json;

namespace RentNearBy.Tests.Trips;

public class TripSegmentContextTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly Guid JourneyId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    // Haridwar days 1-2, Badrinath days 3-4: day 3 is the travel day.
    private static TripPlan TwoCityPlan(int? adults = 3, int? kids = 1)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start, Adults = adults, Kids = kids },
            Legs =
            [
                new TripCityLeg { Index = 1, CityKey = 210000001, CityName = "Haridwar", Latitude = 29.95, Longitude = 78.16 },
                new TripCityLeg { Index = 2, CityKey = 210000002, CityName = "Badrinath", Latitude = 30.74, Longitude = 79.49 },
            ],
        };
        for (var d = 1; d <= 4; d++) plan.Days.Add(new TripDay { Day = d, Leg = d <= 2 ? 1 : 2 });
        return plan;
    }

    private static Trip TwoCityTrip() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Haridwar, Badrinath trip",
        CityName = "Haridwar",
        DayCount = 4,
        StartDate = Start,
    };

    private static TripEnquiryContext Build(TripPlan plan, string? pickupTime = null, string? pickupPlace = null,
        int car = 2, int carOf = 2, int people = 4)
        => TripSegmentContext.Build(TwoCityTrip(), plan, plan.Legs![1], 3, Start.AddDays(2), JourneyId,
            car, carOf, "Haridwar", "Badrinath", null, people, 1, "Rahul", pickupTime, pickupPlace);

    [Fact]
    public void Build_sets_the_leg_city_travel_day_and_journey_car()
    {
        var c = Build(TwoCityPlan());
        Assert.Equal("Badrinath", c.City);
        Assert.Equal(2, c.CityIndex);
        Assert.Equal(2, c.CityCount);
        Assert.Equal(4, c.TripDayCount);
        var day = Assert.Single(c.Days);
        Assert.Equal(3, day.Day);
        Assert.Equal(Start.AddDays(2), day.Date);
        Assert.Equal(JourneyId, c.JourneyId);
        Assert.Equal(2, c.LegIndex);
        Assert.Equal("Haridwar, Badrinath trip", c.TripTitle);
        Assert.Equal("Rahul", c.TravellerName);
    }

    [Fact]
    public void Text_reads_the_car_and_ends_with_the_city_sentence()
    {
        var c = Build(TwoCityPlan());
        Assert.StartsWith("Car 2 of 2: Haridwar to Badrinath, 03 Nov, 4 people.", c.Text);
        Assert.EndsWith(" Part of a 4-day trip, 2 of 2 cities.", c.Text);
    }

    [Fact]
    public void Adults_and_kids_come_from_the_plan_settings()
    {
        var c = Build(TwoCityPlan(adults: 3, kids: 1));
        Assert.Equal(3, c.Adults);
        Assert.Equal(1, c.Kids);
    }

    [Fact]
    public void Without_settings_people_are_adults_and_kids_zero()
    {
        var c = Build(TwoCityPlan(adults: null, kids: null), people: 5);
        Assert.Equal(5, c.Adults);
        Assert.Equal(0, c.Kids);
    }

    [Fact]
    public void Pickup_place_falls_back_to_the_car_start_and_bad_time_is_dropped()
    {
        var c = Build(TwoCityPlan(), pickupTime: "25:99");
        Assert.Null(c.PickupTime);
        Assert.Equal(JourneyLeadText.PickupPlace("Haridwar", 2, 2), c.PickupPlace);

        var given = Build(TwoCityPlan(), pickupTime: "06:30", pickupPlace: "Har ki Pauri gate");
        Assert.Equal("06:30", given.PickupTime);
        Assert.Equal("Har ki Pauri gate", given.PickupPlace);
    }

    [Fact]
    public void Context_round_trips_with_journey_id_and_car_on_the_wire()
    {
        var json = JsonSerializer.Serialize(Build(TwoCityPlan()), TripJson.Options);
        Assert.Contains("\"journeyId\"", json);
        Assert.Contains("\"legIndex\":2", json);
        var back = TripEnquiryContexts.Read(json);
        Assert.Equal(JourneyId, back.JourneyId);
        Assert.Equal(2, back.LegIndex);
    }

    [Fact]
    public void Contexts_read_is_tolerant()
    {
        Assert.Null(TripEnquiryContexts.Read(null).JourneyId);
        Assert.Null(TripEnquiryContexts.Read("not json").JourneyId);
        Assert.Equal(string.Empty, TripEnquiryContexts.Read("").City);
    }

    // ---- segment marker

    private static TripEnquiry Row(string kind = TripEnquiryKinds.Taxi, Guid? tripId = null, short? day = 3, Guid? journeyId = null, int car = 1) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        TripId = tripId ?? Guid.NewGuid(),
        Day = day,
        Status = TripEnquiryStatuses.Asked,
        ContextJson = journeyId is { } j ? $$"""{"city":"Badrinath","journeyId":"{{j}}","legIndex":{{car}}}""" : """{"city":"Haridwar"}""",
    };

    [Fact]
    public void IsSegment_needs_taxi_trip_day_and_journey()
    {
        Assert.True(TripSegmentRows.IsSegment(Row(journeyId: JourneyId)));
        Assert.False(TripSegmentRows.IsSegment(Row(day: null, journeyId: JourneyId)));
        Assert.False(TripSegmentRows.IsSegment(Row(journeyId: null)));
        Assert.False(TripSegmentRows.IsSegment(Row(kind: TripEnquiryKinds.Stay, journeyId: JourneyId)));
        var tripLess = Row(journeyId: JourneyId);
        tripLess.TripId = null;
        Assert.False(TripSegmentRows.IsSegment(tripLess));
    }

    // ---- journey rows of every role (CONTRACT_WIZARD 8.6)

    [Fact]
    public void Segment_build_stores_the_segment_role()
    {
        var c = Build(TwoCityPlan());
        Assert.Equal(TripStepRoles.Segment, c.Role);
        Assert.Contains("\"role\":\"segment\"", JsonSerializer.Serialize(c, TripJson.Options));
    }

    [Fact]
    public void Coming_back_on_the_check_out_morning_names_the_last_city()
    {
        var plan = TwoCityPlan();
        var c = TripSegmentContext.Build(TwoCityTrip(), plan, plan.Legs![1], 5, Start.AddDays(4), JourneyId,
            1, 1, "Badrinath", "Delhi", null, 4, 1, "Rahul", null, null, TripStepRoles.ComingBack);
        Assert.Equal(TripStepRoles.ComingBack, c.Role);
        Assert.Equal("Badrinath", c.City);
        Assert.Equal(5, Assert.Single(c.Days).Day);
        Assert.Contains("05 Nov", c.Text);
    }

    [Fact]
    public void Getting_there_the_day_before_names_the_first_city()
    {
        var plan = TwoCityPlan();
        var c = TripSegmentContext.Build(TwoCityTrip(), plan, plan.Legs![0], 0, Start.AddDays(-1), JourneyId,
            1, 1, "Delhi", "Haridwar", null, 4, 1, "Rahul", null, null, TripStepRoles.GettingThere);
        Assert.Equal(TripStepRoles.GettingThere, c.Role);
        Assert.Equal("Haridwar", c.City);
    }

    [Fact]
    public void Role_of_a_row_defaults_to_segment()
    {
        Assert.Equal(TripStepRoles.Segment, TripSegmentRows.RoleOf(Row(journeyId: JourneyId)));
        var back = Row(day: 0, journeyId: JourneyId);
        back.ContextJson = $$"""{"journeyId":"{{JourneyId}}","legIndex":1,"role":"comingBack"}""";
        Assert.Equal(TripStepRoles.ComingBack, TripSegmentRows.RoleOf(back));
        Assert.True(TripSegmentRows.IsSegment(back));
    }

    [Fact]
    public void A_row_of_a_linked_step_journey_is_another_car_not_stale()
    {
        var trip = Guid.NewGuid();
        var stepJourney = Guid.NewGuid();
        var plan = TwoCityPlan();
        plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = stepJourney } };
        Assert.Equal(TripSegmentRows.RowOther, TripSegmentRows.Classify(Row(tripId: trip, journeyId: stepJourney), plan, JourneyId, 1));
        Assert.Equal(TripSegmentRows.RowStale, TripSegmentRows.Classify(Row(tripId: trip, journeyId: Guid.NewGuid()), plan, JourneyId, 1));
    }

    [Fact]
    public void OfJourney_keeps_only_segment_rows_of_that_journey()
    {
        var other = Guid.NewGuid();
        var rows = new[] { Row(journeyId: JourneyId, car: 1), Row(journeyId: JourneyId, car: 2), Row(journeyId: other), Row(day: null) };
        var mine = TripSegmentRows.OfJourney(rows, JourneyId);
        Assert.Equal(2, mine.Count);
        Assert.Equal(new[] { 1, 2 }, mine.Select(r => TripSegmentRows.CarOf(r)!.Value).OrderBy(x => x).ToArray());
    }
}
