namespace RentNearBy.Tests.Trips;

// One date rule for every journey row (CONTRACT_WIZARD 8.6); the app's TripStepRules.expectedDate mirrors it.
public class TripStepDatesTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);

    // Nainital days 1-2, Almora day 3 (day 3 = travel day of leg 2).
    private static TripPlan Plan(DateOnly? start = null, string? leaveOn = null, DateOnly? gettingThere = null, DateOnly? comingBack = null)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = start },
            Legs =
            [
                new TripCityLeg { Index = 1, CityKey = 1, CityName = "Nainital" },
                new TripCityLeg { Index = 2, CityKey = 2, CityName = "Almora" },
            ],
            Steps = new TripSteps
            {
                GettingThere = new TripStep { Date = gettingThere },
                ComingBack = new TripStep { Date = comingBack, LeaveOn = leaveOn },
            },
        };
        for (var d = 1; d <= 3; d++) plan.Days.Add(new TripDay { Day = d, Leg = d <= 2 ? 1 : 2 });
        return plan;
    }

    [Fact]
    public void Getting_there_defaults_to_the_start()
        => Assert.Equal(Start, TripStepDates.Expected(TripStepRoles.GettingThere, Plan(Start), null));

    [Fact]
    public void Getting_there_own_date_wins_even_the_day_before()
        => Assert.Equal(Start.AddDays(-1), TripStepDates.Expected(TripStepRoles.GettingThere, Plan(Start, gettingThere: Start.AddDays(-1)), null));

    [Fact]
    public void Coming_back_defaults_to_the_check_out_morning()
        => Assert.Equal(Start.AddDays(3), TripStepDates.Expected(TripStepRoles.ComingBack, Plan(Start), null));

    [Fact]
    public void Coming_back_on_the_last_day_evening()
    {
        Assert.Equal(Start.AddDays(2), TripStepDates.Expected(TripStepRoles.ComingBack, Plan(Start, TripLeaveOn.LastDayEvening), null));
        Assert.Equal(Start.AddDays(3), TripStepDates.Expected(TripStepRoles.ComingBack, Plan(Start, TripLeaveOn.CheckOut), null));
    }

    [Fact]
    public void Coming_back_own_date_wins_over_leave_on()
        => Assert.Equal(Start.AddDays(5),
            TripStepDates.Expected(TripStepRoles.ComingBack, Plan(Start, TripLeaveOn.LastDayEvening, comingBack: Start.AddDays(5)), null));

    [Fact]
    public void Segment_is_the_first_day_of_the_city_entered()
    {
        Assert.Equal(Start.AddDays(2), TripStepDates.Expected(TripStepRoles.Segment, Plan(Start), 2));
        Assert.Null(TripStepDates.Expected(TripStepRoles.Segment, Plan(Start), 3));
        Assert.Null(TripStepDates.Expected(TripStepRoles.Segment, Plan(Start), null));
    }

    [Fact]
    public void Without_a_start_only_an_own_step_date_counts()
    {
        Assert.Null(TripStepDates.Expected(TripStepRoles.GettingThere, Plan(), null));
        Assert.Null(TripStepDates.Expected(TripStepRoles.ComingBack, Plan(), null));
        Assert.Null(TripStepDates.Expected(TripStepRoles.Segment, Plan(), 2));
        Assert.Equal(Start, TripStepDates.Expected(TripStepRoles.GettingThere, Plan(gettingThere: Start), null));
    }

    [Fact]
    public void Coming_back_date_follows_leave_on()
    {
        Assert.Equal(Start.AddDays(3), Plan(Start).ComingBackDate);
        Assert.Equal(Start.AddDays(2), Plan(Start, TripLeaveOn.LastDayEvening).ComingBackDate);
        Assert.Equal(Plan(Start).CheckOutDate, Plan(Start).ComingBackDate);
    }

    [Fact]
    public void Leg_for_each_role()
    {
        var plan = Plan(Start);
        Assert.Equal("Nainital", TripStepDates.LegFor(TripStepRoles.GettingThere, plan, null)!.CityName);
        Assert.Equal("Almora", TripStepDates.LegFor(TripStepRoles.ComingBack, plan, null)!.CityName);
        Assert.Equal("Almora", TripStepDates.LegFor(TripStepRoles.Segment, plan, 2)!.CityName);
        Assert.Null(TripStepDates.LegFor(TripStepRoles.Segment, plan, 5));
    }
}
