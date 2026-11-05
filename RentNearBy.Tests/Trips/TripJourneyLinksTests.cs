namespace RentNearBy.Tests.Trips;

// CONTRACT_MCB D1/D3: the plan is the only place that says which travel a journey is.
public class TripJourneyLinksTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly Guid StepId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid LegTwoId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid LegThreeId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    // Haridwar days 1-2, Badrinath days 3-4, Rishikesh day 5.
    private static TripPlan Plan(TripTravel? legTwo = null, TripTravel? legThree = null, Guid? gettingThere = null)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs =
            [
                new TripCityLeg { Index = 1, CityName = "Haridwar" },
                new TripCityLeg { Index = 2, CityName = "Badrinath", Arrival = legTwo },
                new TripCityLeg { Index = 3, CityName = "Rishikesh", Arrival = legThree },
            ],
        };
        int[] legs = [1, 1, 2, 2, 3];
        for (var d = 1; d <= legs.Length; d++) plan.Days.Add(new TripDay { Day = d, Leg = legs[d - 1] });
        if (gettingThere is { } id) plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = id } };
        return plan;
    }

    private static TripTravel Journey(Guid id) => new() { Mode = TripTravelModes.Journey, FromName = "Haridwar", JourneyId = id, Cars = 2 };

    private static TripTravel Own() => new() { Mode = TripTravelModes.Own, FromName = "Haridwar", How = TripTravelHows.Train };

    [Fact]
    public void Step_journey_has_a_role_without_a_leg()
    {
        var link = TripJourneyLinks.Resolve(Plan(gettingThere: StepId), StepId);

        Assert.NotNull(link);
        Assert.Equal(TripStepRoles.GettingThere, link!.Role);
        Assert.Null(link.LegIndex);
        Assert.Null(link.TravelDay);
    }

    [Fact]
    public void Leg_two_arrival_is_a_segment_on_the_first_day_of_the_leg()
    {
        var plan = Plan(Journey(LegTwoId));
        var link = TripJourneyLinks.Resolve(plan, LegTwoId);

        Assert.NotNull(link);
        Assert.Equal(TripStepRoles.Segment, link!.Role);
        Assert.Equal(2, link.LegIndex);
        Assert.Equal(plan.LegStart(2), link.TravelDay);
        Assert.Equal(3, link.TravelDay);
    }

    [Fact]
    public void Leg_three_travel_day_is_its_first_day()
    {
        var link = TripJourneyLinks.Resolve(Plan(null, Journey(LegThreeId)), LegThreeId);

        Assert.Equal(3, link!.LegIndex);
        Assert.Equal(5, link.TravelDay);
    }

    [Fact]
    public void Unknown_journey_has_no_link()
    {
        Assert.Null(TripJourneyLinks.Resolve(Plan(Journey(LegTwoId)), Guid.NewGuid()));
        Assert.Null(TripJourneyLinks.Resolve(Plan(Journey(LegTwoId)), Guid.Empty));
    }

    [Fact]
    public void Own_arrival_is_never_a_segment_journey()
        => Assert.Empty(TripTravelRules.ArrivalIds(Plan(Own())));

    [Fact]
    public void Referenced_ids_are_steps_then_arrivals_without_duplicates()
    {
        var ids = TripJourneyLinks.ReferencedIds(Plan(Journey(LegTwoId), Journey(StepId), StepId));

        Assert.Equal(new[] { StepId, LegTwoId }, ids);
    }

    [Fact]
    public void Id_in_a_step_and_a_leg_stays_a_step_after_sanitise()
    {
        var plan = Plan(Journey(StepId), null, StepId);
        var keep = TripStepRules.Sanitise(plan.Steps, new HashSet<Guid> { StepId });

        TripTravelRules.SanitiseArrivals(plan, new HashSet<Guid> { StepId }, keep);

        Assert.Null(plan.Legs![1].Arrival);
        Assert.Equal(TripStepRoles.GettingThere, TripJourneyLinks.Resolve(plan, StepId)!.Role);
        Assert.Equal(new[] { StepId }, keep);
    }

    [Fact]
    public void Sanitise_keeps_owned_arrivals()
    {
        var plan = Plan(Journey(LegTwoId), Journey(LegThreeId));
        var keep = new List<Guid>();

        TripTravelRules.SanitiseArrivals(plan, new HashSet<Guid> { LegTwoId, LegThreeId }, keep);

        Assert.Equal(LegTwoId, plan.Legs![1].Arrival!.JourneyId);
        Assert.Equal(LegThreeId, plan.Legs[2].Arrival!.JourneyId);
        Assert.Equal(new[] { LegTwoId, LegThreeId }, keep);
    }

    [Fact]
    public void Sanitise_drops_a_foreign_or_not_owned_arrival()
    {
        var plan = Plan(Journey(LegTwoId), Journey(LegThreeId));
        var keep = new List<Guid>();

        TripTravelRules.SanitiseArrivals(plan, new HashSet<Guid> { LegThreeId }, keep);

        Assert.Null(plan.Legs![1].Arrival);
        Assert.NotNull(plan.Legs[2].Arrival);
        Assert.Equal(new[] { LegThreeId }, keep);
    }

    [Fact]
    public void Sanitise_drops_a_later_duplicate()
    {
        var plan = Plan(Journey(LegTwoId), Journey(LegTwoId));
        var keep = new List<Guid>();

        TripTravelRules.SanitiseArrivals(plan, new HashSet<Guid> { LegTwoId }, keep);

        Assert.NotNull(plan.Legs![1].Arrival);
        Assert.Null(plan.Legs[2].Arrival);
        Assert.Equal(2, TripJourneyLinks.Resolve(plan, LegTwoId)!.LegIndex);
    }

    [Fact]
    public void Sanitise_drops_a_journey_arrival_without_an_id()
    {
        var travel = Journey(LegTwoId);
        travel.JourneyId = null;
        var plan = Plan(travel);

        TripTravelRules.SanitiseArrivals(plan, new HashSet<Guid>(), new List<Guid>());

        Assert.Null(plan.Legs![1].Arrival);
    }

    [Fact]
    public void Sanitise_leaves_own_arrivals_untouched()
    {
        var own = Own();
        var plan = Plan(own);
        var keep = new List<Guid>();

        TripTravelRules.SanitiseArrivals(plan, new HashSet<Guid>(), keep);

        Assert.Same(own, plan.Legs![1].Arrival);
        Assert.Empty(keep);
    }

    [Fact]
    public void Segment_role_is_not_a_step_role()
    {
        Assert.Equal("segment", TripStepRoles.Segment);
        Assert.DoesNotContain(TripStepRoles.Segment, TripStepRoles.All);
    }
}
