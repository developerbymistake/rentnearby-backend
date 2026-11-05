namespace RentNearBy.Tests.Trips;

// Haridwar days 1-2, Badrinath days 3-4 (day 3 = travel day); start 01 Nov, check-out 05 Nov.
public class TripLegRulesTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private const double HaridwarLat = 29.9457, HaridwarLon = 78.1642, BadrinathLat = 30.7433, BadrinathLon = 79.4938;

    private static DateOnly D(int day) => Start.AddDays(day - 1);

    private static TripPlan TwoCity(DateOnly? start = null)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = start ?? Start },
            Legs =
            [
                new TripCityLeg { Index = 1, CityKey = 1, CityName = "Haridwar", Latitude = HaridwarLat, Longitude = HaridwarLon },
                new TripCityLeg { Index = 2, CityKey = 2, CityName = "Badrinath", Latitude = BadrinathLat, Longitude = BadrinathLon },
            ],
        };
        for (var d = 1; d <= 4; d++) plan.Days.Add(new TripDay { Day = d, Leg = d <= 2 ? 1 : 2 });
        plan.Normalize();
        return plan;
    }

    private static TripPlan OneCity(int days)
    {
        var plan = new TripPlan { CityKey = 1, CityName = "Haridwar", Settings = new TripSettings { StartDate = Start } };
        for (var d = 1; d <= days; d++) plan.Days.Add(new TripDay { Day = d });
        plan.Normalize();
        return plan;
    }

    private static TripEnquiry StayRow(int fromNight, int toNight, string? city = "Haridwar") => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        Status = TripEnquiryStatuses.ConfirmedByBoth,
        Day = (short)fromNight,
        CheckIn = D(fromNight),
        CheckOut = D(toNight + 1),
        ContextJson = city == null ? "{}" : $$"""{"city":"{{city}}"}""",
    };

    private static TripEnquiry TaxiRow(string? daysJson, DateOnly checkIn, DateOnly checkOut, string city = "Haridwar") => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = TripEnquiryStatuses.BookedByTraveller,
        DaysJson = daysJson,
        CheckIn = checkIn,
        CheckOut = checkOut,
        ContextJson = $$"""{"city":"{{city}}"}""",
    };

    private static TripEnquiry SegmentRow() => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = TripEnquiryStatuses.BookedByTraveller,
        Day = 3,
        DaysJson = "[3]",
        CheckIn = D(1),
        CheckOut = D(2),
        ContextJson = $$"""{"city":"Haridwar","journeyId":"{{Guid.NewGuid()}}","legIndex":1}""",
    };

    // ---- dates

    [Fact]
    public void DatesOf_stay_is_check_in_to_the_night_before_check_out()
        => Assert.Equal(new[] { D(1), D(2) }, TripLegRules.DatesOf(StayRow(1, 2)));

    [Fact]
    public void DatesOf_taxi_follows_check_in_with_the_gaps_of_its_days()
        => Assert.Equal(new[] { D(2), D(4) }, TripLegRules.DatesOf(TaxiRow("[2,4]", D(2), D(5))));

    [Fact]
    public void DatesOf_taxi_without_days_is_check_in_to_check_out_minus_one()
        => Assert.Equal(new[] { D(1), D(2) }, TripLegRules.DatesOf(TaxiRow(null, D(1), D(3))));

    [Fact]
    public void DatesOf_a_row_without_check_in_is_empty()
    {
        var row = StayRow(1, 1);
        row.CheckIn = null;
        Assert.Empty(TripLegRules.DatesOf(row));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    public void DayOf_inside(int offset, int expected)
        => Assert.Equal(expected, TripLegRules.DayOf(Start, Start.AddDays(offset), 4));

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void DayOf_outside_is_null(int offset)
        => Assert.Null(TripLegRules.DayOf(Start, Start.AddDays(offset), 4));

    // ---- nights per leg

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    public void LegEndNight_is_the_last_day_of_the_leg(int day, int expected)
        => Assert.Equal(expected, TripLegRules.LegEndNight(TwoCity(), day));

    [Fact]
    public void LegEndNight_without_days_is_the_day()
        => Assert.Equal(3, TripLegRules.LegEndNight(new TripPlan(), 3));

    [Fact]
    public void All_nights_from_leg_one_end_on_the_travel_day_morning()
        => Assert.Equal(D(3), TripLegRules.StayCheckOut(TwoCity(), Start, 1, allNights: true, nights: null));

    [Fact]
    public void All_nights_in_the_last_leg_end_on_the_trip_check_out()
        => Assert.Equal(Start.AddDays(4), TripLegRules.StayCheckOut(TwoCity(), Start, 3, allNights: true, nights: null));

    [Fact]
    public void Explicit_nights_beyond_the_leg_are_clamped_to_the_leg()
    {
        Assert.Equal(D(3), TripLegRules.StayCheckOut(TwoCity(), Start, 1, allNights: false, nights: 4));
        Assert.Equal(D(2), TripLegRules.StayCheckOut(TwoCity(), Start, 1, allNights: false, nights: null));
        Assert.Equal(D(2), TripLegRules.StayCheckOut(TwoCity(), Start, 1, allNights: true, nights: 0));
    }

    [Theory]
    [InlineData(1, true, null)]
    [InlineData(2, true, null)]
    [InlineData(4, true, null)]
    [InlineData(1, false, null)]
    [InlineData(4, false, null)]
    [InlineData(1, false, 2)]
    [InlineData(2, false, 9)]
    [InlineData(3, true, 1)]
    public void One_city_plan_equals_the_trip_rule(int day, bool allNights, int? nights)
    {
        var plan = OneCity(4);
        Assert.Equal(TripDateRules.StayCheckOut(Start, 4, day, allNights, nights),
            TripLegRules.StayCheckOut(plan, Start, day, allNights, nights));
    }

    [Fact]
    public void One_city_legless_plan_equals_the_trip_rule()
    {
        var plan = new TripPlan { Settings = new TripSettings { StartDate = Start } };
        for (var d = 1; d <= 3; d++) plan.Days.Add(new TripDay { Day = d });
        Assert.Equal(TripDateRules.StayCheckOut(Start, 3, 1, true, null), TripLegRules.StayCheckOut(plan, Start, 1, true, null));
    }

    [Fact]
    public void DaysInOneLeg_cases()
    {
        var plan = TwoCity();
        Assert.True(TripLegRules.DaysInOneLeg(plan, [3, 4]));
        Assert.True(TripLegRules.DaysInOneLeg(plan, [1, 2]));
        Assert.False(TripLegRules.DaysInOneLeg(plan, [1, 3]));
        Assert.False(TripLegRules.DaysInOneLeg(plan, [2, 3]));
        Assert.True(TripLegRules.DaysInOneLeg(plan, [2]));
        Assert.True(TripLegRules.DaysInOneLeg(plan, []));
    }

    // ---- no stay tonight

    [Fact]
    public void IsNoStayNight_reads_the_skipped_stay()
    {
        var plan = TwoCity();
        plan.Days[1].Stay = new TripStayChoice { Name = "No stay tonight", Skipped = true };
        Assert.True(TripLegRules.IsNoStayNight(plan, 2));
        Assert.False(TripLegRules.IsNoStayNight(plan, 1));
        Assert.False(TripLegRules.IsNoStayNight(plan, 9));
    }

    [Fact]
    public void FirstNoStayNight_first_and_later_night()
    {
        var plan = TwoCity();
        plan.Days[1].Stay = new TripStayChoice { Name = "No stay tonight", Skipped = true };
        Assert.Equal(2, TripLegRules.FirstNoStayNight(plan, Start, D(2), D(3)));
        Assert.Equal(2, TripLegRules.FirstNoStayNight(plan, Start, D(1), D(3)));
        Assert.Null(TripLegRules.FirstNoStayNight(plan, Start, D(1), D(2)));
        Assert.Null(TripLegRules.FirstNoStayNight(plan, Start, D(3), D(5)));

        plan.Days[0].Stay = new TripStayChoice { Name = "No stay tonight", Skipped = true };
        Assert.Equal(1, TripLegRules.FirstNoStayNight(plan, Start, D(1), D(3)));
    }

    // ---- home city

    [Fact]
    public void HomeLegIndex_by_context_city_fold()
    {
        var plan = TwoCity();
        Assert.Equal(1, TripLegRules.HomeLegIndex(StayRow(1, 1, "haridwar"), plan));
        Assert.Equal(2, TripLegRules.HomeLegIndex(StayRow(1, 1, " BADRINATH "), plan));
    }

    [Fact]
    public void HomeLegIndex_by_nearest_coordinates_when_city_is_empty()
    {
        var row = StayRow(1, 1, city: null);
        row.ListingLatitude = 30.74;
        row.ListingLongitude = 79.49;
        Assert.Equal(2, TripLegRules.HomeLegIndex(row, TwoCity()));

        row.ListingLatitude = 29.95;
        row.ListingLongitude = 78.16;
        Assert.Equal(1, TripLegRules.HomeLegIndex(row, TwoCity()));
    }

    [Fact]
    public void HomeLegIndex_unknown_city_and_no_coordinates_is_null()
    {
        Assert.Null(TripLegRules.HomeLegIndex(StayRow(1, 1, city: null), TwoCity()));
        Assert.Null(TripLegRules.HomeLegIndex(StayRow(1, 1, "Delhi"), TwoCity()));
    }

    // ---- wrong city

    [Fact]
    public void Haridwar_row_on_a_night_now_in_Badrinath_is_wrong()
    {
        var w = TripLegRules.WrongCity(StayRow(3, 3), TwoCity(), Start);
        Assert.True(w.Any);
        Assert.Equal(1, w.Wrong);
        Assert.Equal(1, w.Total);
        Assert.Equal(3, w.FirstWrongDay);
        Assert.Equal("Badrinath", w.DayCity);
    }

    [Fact]
    public void Three_night_row_with_one_night_moved_is_one_of_three()
    {
        var w = TripLegRules.WrongCity(StayRow(1, 3), TwoCity(), Start);
        Assert.Equal(1, w.Wrong);
        Assert.Equal(3, w.Total);
        Assert.Equal(3, w.FirstWrongDay);
        Assert.Equal("Badrinath", w.DayCity);
    }

    [Fact]
    public void Same_city_is_not_wrong()
    {
        var w = TripLegRules.WrongCity(StayRow(1, 2), TwoCity(), Start);
        Assert.False(w.Any);
        Assert.Equal(0, w.Wrong);
        Assert.Equal(2, w.Total);
        Assert.Null(w.FirstWrongDay);
        Assert.False(TripLegRules.WrongCity(StayRow(3, 4, "Badrinath"), TwoCity(), Start).Any);
    }

    [Fact]
    public void Local_taxi_days_in_the_other_city_are_wrong()
    {
        var w = TripLegRules.WrongCity(TaxiRow("[2,3]", D(2), D(4)), TwoCity(), Start);
        Assert.Equal(1, w.Wrong);
        Assert.Equal(2, w.Total);
        Assert.Equal(3, w.FirstWrongDay);
    }

    [Fact]
    public void One_city_plan_is_never_wrong()
        => Assert.Equal(TripWrongCity.None, TripLegRules.WrongCity(StayRow(1, 3, "Rishikesh"), OneCity(4), Start));

    [Fact]
    public void Segment_row_is_never_wrong()
        => Assert.Equal(TripWrongCity.None, TripLegRules.WrongCity(SegmentRow(), TwoCity(), Start));

    [Fact]
    public void No_start_is_never_wrong()
        => Assert.Equal(TripWrongCity.None, TripLegRules.WrongCity(StayRow(3, 3), TwoCity(), null));

    [Fact]
    public void Unknown_home_is_never_wrong()
        => Assert.Equal(TripWrongCity.None, TripLegRules.WrongCity(StayRow(3, 3, "Delhi"), TwoCity(), Start));

    [Fact]
    public void Nights_outside_the_trip_are_not_counted_as_wrong()
    {
        var w = TripLegRules.WrongCity(StayRow(4, 5, "Badrinath"), TwoCity(), Start);
        Assert.Equal(0, w.Wrong);
        Assert.Equal(1, w.Total);
    }

    // ---- outside trip

    [Fact]
    public void Row_before_a_moved_start_is_outside()
        => Assert.True(TripLegRules.OutsideTrip(StayRow(1, 1), TwoCity(), Start.AddDays(2)));

    [Fact]
    public void Row_inside_is_not_outside_and_the_last_night_is_inside()
    {
        Assert.False(TripLegRules.OutsideTrip(StayRow(1, 2), TwoCity(), Start));
        Assert.False(TripLegRules.OutsideTrip(StayRow(4, 4, "Badrinath"), TwoCity(), Start));
        Assert.True(TripLegRules.OutsideTrip(StayRow(4, 5, "Badrinath"), TwoCity(), Start));
    }

    [Fact]
    public void No_start_is_never_outside()
        => Assert.False(TripLegRules.OutsideTrip(StayRow(1, 1), TwoCity(), null));

    [Fact]
    public void Segment_row_is_never_outside()
        => Assert.False(TripLegRules.OutsideTrip(SegmentRow(), TwoCity(), Start.AddDays(5)));
}
