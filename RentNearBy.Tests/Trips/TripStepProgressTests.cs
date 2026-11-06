namespace RentNearBy.Tests.Trips;

// Shared vectors V1-V16 of CONTRACT_WIZARD 9.3 (the app's trip_wizard_rules_test.dart holds the same values).
public class TripStepProgressTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly Guid GettingThereId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid ComingBackId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");

    private static DateOnly DateOf(int day) => Start.AddDays(day - 1);

    // Nainital days 1-2, Almora day 3; basics done, places on every day (the auto split).
    private static TripPlan ThreeDayTwoCity()
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start, Adults = 2 },
            Steps = new TripSteps { StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 } },
            Legs =
            [
                new TripCityLeg { Index = 1, CityKey = 1, CityName = "Nainital" },
                new TripCityLeg { Index = 2, CityKey = 2, CityName = "Almora" },
            ],
        };
        for (var d = 1; d <= 3; d++) plan.Days.Add(Day(d, d <= 2 ? 1 : 2, spots: 1));
        return plan;
    }

    private static TripPlan OneCity(int days, int spots = 1)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start, Adults = 2 },
            Steps = new TripSteps { StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 } },
            Legs = [new TripCityLeg { Index = 1, CityKey = 1, CityName = "Nainital" }],
        };
        for (var d = 1; d <= days; d++) plan.Days.Add(Day(d, 1, spots));
        return plan;
    }

    private static TripDay Day(int d, int leg, int spots)
    {
        var day = new TripDay { Day = d, Leg = leg };
        for (var i = 0; i < spots; i++) day.Spots.Add(new TripSpot { Id = $"s{d}{i}", Name = "Spot", VisitMinutes = 60 });
        return day;
    }

    private static Trip TripOf(TripPlan plan) => new()
    {
        Id = Guid.NewGuid(), Status = TripStatuses.Planned, StartDate = plan.Settings.StartDate, EndDate = plan.EndDate,
    };

    private static TripEnquiry StayRow(string status, int night, int nights = 1) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        Status = status,
        Day = (short)night,
        CheckIn = DateOf(night),
        CheckOut = DateOf(night + nights),
        AdvanceStatus = TripAdvanceStatuses.None,
    };

    private static TripEnquiry TaxiRow(string status, int day) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = status,
        DaysJson = $"[{day}]",
        CheckIn = DateOf(day),
        CheckOut = DateOf(day + 1),
        AdvanceStatus = TripAdvanceStatuses.None,
    };

    private static TripEnquiry JourneyRow(string status, Guid journeyId, int car, DateOnly date, string role) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = status,
        Day = TripExecutionRules.DayOf(Start, date),
        CheckIn = date,
        CheckOut = date.AddDays(1),
        AdvanceStatus = TripAdvanceStatuses.None,
        ContextJson = $$"""{"city":"Nainital","journeyId":"{{journeyId}}","legIndex":{{car}},"role":"{{role}}"}""",
    };

    // Saved, nobody asked.
    private static SavedJourney Journey(Guid id, int cars, DateOnly date)
    {
        var journey = new SavedJourney { Id = id, TravelDate = date, Cars = (short)cars };
        for (var car = 1; car <= cars; car++) journey.Legs.Add(new SavedJourneyLeg { LegIndex = (short)car, DriverAvailable = true });
        return journey;
    }

    private static void ChooseGettingThere(TripPlan plan, List<int>? ownCars = null)
        => plan.Steps!.GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = GettingThereId, OwnCars = ownCars };

    private static List<TripStepInfo> Steps(TripPlan plan, IReadOnlyList<TripEnquiry> rows, params SavedJourney[] journeys)
        => TripStepProgress.Steps(TripOf(plan), plan, rows, journeys);

    private static TripProgress Progress(TripPlan plan, IReadOnlyList<TripEnquiry> rows, params SavedJourney[] journeys)
        => TripStepProgress.Compute(TripOf(plan), plan, rows, journeys);

    private static TripStepInfo Step(IEnumerable<TripStepInfo> steps, string key) => steps.Single(s => s.Key == key);

    // ---- step list

    [Fact]
    public void Step_list_order_and_keys_for_two_cities()
    {
        var keys = Steps(ThreeDayTwoCity(), []).Select(s => s.Key).ToArray();
        Assert.Equal(
            new[]
            {
                "basics", "gettingThere",
                "places:1:1", "taxi:1:1", "stay:1:1", "places:1:2", "taxi:1:2", "stay:1:2",
                "travel:2", "places:2:1", "taxi:2:1", "stay:2:1",
                "comingBack",
            }, keys);
    }

    [Theory]
    [InlineData(1, 1, 6)]
    [InlineData(3, 2, 13)]
    [InlineData(14, 4, 48)]
    public void Total_is_three_per_day_plus_cities_plus_two(int days, int cities, int total)
    {
        var plan = OneCity(days);
        plan.Legs = Enumerable.Range(1, cities).Select(k => new TripCityLeg { Index = k, CityKey = k, CityName = $"C{k}" }).ToList();
        for (var d = 0; d < days; d++) plan.Days[d].Leg = Math.Min(cities, 1 + d * cities / days);
        Assert.Equal(total, Steps(plan, []).Count);
        Assert.Equal(3 * days + cities + 2, total);
    }

    [Fact]
    public void Every_step_is_offered_on_every_day_with_no_auto_not_needed()
    {
        var steps = Steps(OneCity(1), []);
        Assert.Equal(new[] { "basics", "gettingThere", "places:1:1", "taxi:1:1", "stay:1:1", "comingBack" }, steps.Select(s => s.Key));
        Assert.All(steps.Where(s => s.Kind != TripStepKinds.Basics && s.Kind != TripStepKinds.Places),
            s => Assert.Equal(TripStepStates.Todo, s.State));
    }

    // ---- shared vectors

    private static (TripPlan Plan, List<TripEnquiry> Rows, SavedJourney[] Journeys) V2()
    {
        var plan = ThreeDayTwoCity();
        ChooseGettingThere(plan);
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "Own car", IsOwn = true };
        return (plan, [StayRow(TripEnquiryStatuses.Asked, 1)], [Journey(GettingThereId, 2, Start)]);
    }

    private static (TripPlan Plan, List<TripEnquiry> Rows, SavedJourney[] Journeys) V3()
    {
        var (plan, _, journeys) = V2();
        List<TripEnquiry> rows =
        [
            StayRow(TripEnquiryStatuses.ConfirmedByBoth, 1),
            JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, GettingThereId, 1, Start, TripStepRoles.GettingThere),
            JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, GettingThereId, 2, Start, TripStepRoles.GettingThere),
        ];
        return (plan, rows, journeys);
    }

    private static (TripPlan Plan, List<TripEnquiry> Rows, SavedJourney[] Journeys) V4()
    {
        var (plan, rows, journeys) = V3();
        rows.Add(StayRow(TripEnquiryStatuses.ConfirmedByTraveller, 2));
        rows.Add(TaxiRow(TripEnquiryStatuses.BookedByTraveller, 2));
        return (plan, rows, journeys);
    }

    private static (TripPlan Plan, List<TripEnquiry> Rows, SavedJourney[] Journeys) V5()
    {
        var (plan, rows, journeys) = V4();
        plan.Legs![1].Arrival = new TripTravel { Mode = TripTravelModes.Own, FromName = "Nainital", How = TripTravelHows.Bus };
        plan.Steps!.ComingBack = new TripStep { Status = TripStepStatuses.Own, Note = "Train" };
        rows.Add(TaxiRow(TripEnquiryStatuses.ConfirmedByBoth, 3));
        rows.Add(StayRow(TripEnquiryStatuses.ConfirmedByBoth, 3));
        return (plan, rows, journeys);
    }

    [Fact]
    public void V1_fresh_plan_after_the_auto_split()
    {
        var p = Progress(ThreeDayTwoCity(), []);
        Assert.Equal(13, p.Total);
        Assert.Equal(4, p.Counted);
        Assert.Equal(30, p.Percent);
        Assert.Equal(0, p.Decided);
        Assert.Equal(TripStepKinds.GettingThere, p.Next!.Key);
        Assert.False(p.NeedsYou);
    }

    [Fact]
    public void V2_saved_journey_nobody_asked_needs_you_first()
    {
        var (plan, rows, journeys) = V2();
        var p = Progress(plan, rows, journeys);
        Assert.Equal(5, p.Counted);
        Assert.Equal(38, p.Percent);
        Assert.Equal(1, p.NeedYou);
        Assert.Equal(1, p.WaitingForReply);
        Assert.Equal(2, p.Waiting);
        Assert.Equal(1, p.Decided);
        Assert.Equal(TripStepKinds.GettingThere, p.Next!.Key);

        var steps = Steps(plan, rows, journeys);
        Assert.Equal((TripStepStates.Waiting, true), (Step(steps, "gettingThere").State, Step(steps, "gettingThere").NeedsYou));
        Assert.Equal((TripStepStates.Waiting, false), (Step(steps, "stay:1:1").State, Step(steps, "stay:1:1").NeedsYou));
        Assert.Equal(TripStepStates.Own, Step(steps, "taxi:1:1").State);
    }

    [Fact]
    public void V3_confirmed_night_and_both_cars_confirmed()
    {
        var (plan, rows, journeys) = V3();
        var p = Progress(plan, rows, journeys);
        Assert.Equal(7, p.Counted);
        Assert.Equal(53, p.Percent);
        Assert.Equal("taxi:1:2", p.Next!.Key);
        Assert.Equal(2, p.Next.Day);
        Assert.Equal(TripStepKinds.Taxi, p.Next.Kind);
    }

    [Fact]
    public void V4_booked_by_you_and_confirmed_by_you_wait_for_a_reply()
    {
        var (plan, rows, journeys) = V4();
        var p = Progress(plan, rows, journeys);
        Assert.Equal(7, p.Counted);
        Assert.Equal(53, p.Percent);
        Assert.Equal("travel:2", p.Next!.Key);
        Assert.Equal(2, p.Next.LegIndex);

        var steps = Steps(plan, rows, journeys);
        foreach (var key in new[] { "taxi:1:2", "stay:1:2" })
        {
            Assert.Equal(TripStepStates.Waiting, Step(steps, key).State);
            Assert.False(Step(steps, key).NeedsYou);
        }
    }

    [Fact]
    public void V5_only_waiting_steps_left_next_is_the_first_waiting()
    {
        var (plan, rows, journeys) = V5();
        var p = Progress(plan, rows, journeys);
        Assert.Equal(11, p.Counted);
        Assert.Equal(84, p.Percent);
        Assert.Equal("taxi:1:2", p.Next!.Key);
    }

    [Fact]
    public void V6_everything_counted_is_100_and_ready()
    {
        var (plan, rows, journeys) = V5();
        rows.RemoveAll(r => r.Status is TripEnquiryStatuses.ConfirmedByTraveller or TripEnquiryStatuses.BookedByTraveller);
        rows.Add(StayRow(TripEnquiryStatuses.ConfirmedByBoth, 2));
        rows.Add(TaxiRow(TripEnquiryStatuses.ConfirmedByBoth, 2));
        var p = Progress(plan, rows, journeys);
        Assert.Equal(13, p.Counted);
        Assert.Equal(100, p.Percent);
        Assert.Null(p.Next);
        Assert.Equal(TripBookingStates.Ready, TripBookingStates.Compute(TripOf(plan), plan, rows, journeys));
    }

    [Fact]
    public void V7_one_day_trip_no_stay_and_coming_back_own_in_the_evening()
    {
        var plan = OneCity(1, spots: 0);
        plan.Days[0].Stay = new TripStayChoice { Name = "No stay tonight", Skipped = true };
        plan.Steps!.ComingBack = new TripStep { Status = TripStepStatuses.Own, LeaveOn = TripLeaveOn.LastDayEvening };
        var p = Progress(plan, []);
        Assert.Equal(6, p.Total);
        Assert.Equal(3, p.Counted);
        Assert.Equal(50, p.Percent);
        Assert.Equal(TripStepKinds.GettingThere, p.Next!.Key);
    }

    [Fact]
    public void V8_dismissed_problem_reads_not_sorted()
    {
        var plan = OneCity(2);
        var expired = StayRow(TripEnquiryStatuses.Expired, 1);
        Assert.Equal(TripStepStates.Attention, Step(Steps(plan, [expired]), "stay:1:1").State);

        plan.DismissedEnquiryIds = [expired.Id];
        Assert.Equal(TripStepStates.Todo, Step(Steps(plan, [expired]), "stay:1:1").State);
    }

    [Fact]
    public void V9_stored_not_needed_coming_back_is_todo()
    {
        var plan = OneCity(2);
        plan.Steps!.ComingBack = new TripStep { Status = TripStepStatuses.NotNeeded };
        Assert.Equal(TripStepStates.Todo, Step(Steps(plan, []), "comingBack").State);
        plan.Normalize();
        Assert.Equal(TripStepStatuses.NotStarted, plan.Steps.ComingBack.Status);
        Assert.Equal(TripStepStates.Todo, Step(Steps(plan, []), "comingBack").State);
    }

    [Fact]
    public void V10_same_as_last_night_covered_by_the_booking_is_done()
    {
        var plan = OneCity(3);
        plan.Days[2].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Hill View", SameAsDay = 2 };
        var steps = Steps(plan, [StayRow(TripEnquiryStatuses.ConfirmedByBoth, 2, nights: 2)]);
        Assert.Equal(TripStepStates.Done, Step(steps, "stay:1:3").State);
    }

    [Fact]
    public void V11_same_as_last_night_not_covered_needs_you()
    {
        var plan = OneCity(3);
        plan.Days[2].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Hill View", SameAsDay = 2 };
        var night = Step(Steps(plan, [StayRow(TripEnquiryStatuses.ConfirmedByBoth, 2)]), "stay:1:3");
        Assert.Equal(TripStepStates.Waiting, night.State);
        Assert.True(night.NeedsYou);
    }

    [Fact]
    public void V12_own_stay_and_a_confirmed_booking_on_one_night_is_two_plans()
    {
        var plan = OneCity(3);
        plan.Days[1].Stay = new TripStayChoice { Name = "Cousin's house", IsOwn = true };
        var steps = Steps(plan, [StayRow(TripEnquiryStatuses.ConfirmedByBoth, 2)]);
        var night = Step(steps, "stay:1:2");
        Assert.Equal(TripStepStates.Attention, night.State);
        Assert.True(night.NeedsYou);
        Assert.False(night.Counted);
    }

    [Fact]
    public void V13_one_car_confirmed_one_booked_by_you_waits_for_a_reply()
    {
        var plan = OneCity(2);
        ChooseGettingThere(plan);
        TripEnquiry[] rows =
        [
            JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, GettingThereId, 1, Start, TripStepRoles.GettingThere),
            JourneyRow(TripEnquiryStatuses.BookedByTraveller, GettingThereId, 2, Start, TripStepRoles.GettingThere),
        ];
        var step = Step(Steps(plan, rows, Journey(GettingThereId, 2, Start)), "gettingThere");
        Assert.Equal(TripStepStates.Waiting, step.State);
        Assert.False(step.NeedsYou);
    }

    [Fact]
    public void V14_one_car_confirmed_and_the_other_own_is_done()
    {
        var plan = OneCity(2);
        ChooseGettingThere(plan, ownCars: [2]);
        TripEnquiry[] rows = [JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, GettingThereId, 1, Start, TripStepRoles.GettingThere)];
        Assert.Equal(TripStepStates.Done, Step(Steps(plan, rows, Journey(GettingThereId, 2, Start)), "gettingThere").State);
    }

    [Fact]
    public void V15_coming_back_on_the_last_evening_keeps_its_date()
    {
        var plan = OneCity(2);
        plan.Steps!.ComingBack = new TripStep
        {
            Status = TripStepStatuses.Chosen, JourneyId = ComingBackId, LeaveOn = TripLeaveOn.LastDayEvening,
        };
        var lastDay = DateOf(2);
        TripEnquiry[] rows = [JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, ComingBackId, 1, lastDay, TripStepRoles.ComingBack)];
        var journey = Journey(ComingBackId, 1, lastDay);

        Assert.Equal(lastDay, TripStepDates.Expected(TripStepRoles.ComingBack, plan, null));
        Assert.Equal(TripStepStates.Done, Step(Steps(plan, rows, journey), "comingBack").State);

        plan.Steps.ComingBack.LeaveOn = TripLeaveOn.CheckOut;
        Assert.Equal(TripStepStates.Attention, Step(Steps(plan, rows, journey), "comingBack").State);
    }

    [Fact]
    public void V16_unasked_shortlist_needs_you_before_a_later_todo()
    {
        var plan = OneCity(2);
        plan.Days[0].StayShortlist =
        [
            new TripStayShortlistItem { StayId = Guid.NewGuid(), Name = "Deodar Homestay" },
            new TripStayShortlistItem { StayId = Guid.NewGuid(), Name = "Hill View" },
        ];
        var steps = Steps(plan, []);
        Assert.Equal(TripStepStates.Waiting, Step(steps, "stay:1:1").State);
        Assert.True(Step(steps, "stay:1:1").NeedsYou);
        Assert.Equal("stay:1:1", TripStepProgress.Summarise(steps).Next!.Key);
    }

    // ---- more rows of the mapping table

    [Fact]
    public void Chosen_stay_nobody_asked_needs_you_and_is_not_counted()
    {
        var plan = OneCity(1);
        plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Deodar Homestay" };
        var night = Step(Steps(plan, []), "stay:1:1");
        Assert.Equal(TripStepStates.Waiting, night.State);
        Assert.True(night.NeedsYou);
        Assert.False(night.Counted);
    }

    [Fact]
    public void An_offer_needs_you_and_a_fare_needs_you()
    {
        var plan = OneCity(1);
        var steps = Steps(plan, [StayRow(TripEnquiryStatuses.Offered, 1), TaxiRow(TripEnquiryStatuses.FareSent, 1)]);
        Assert.True(Step(steps, "stay:1:1").NeedsYou);
        Assert.True(Step(steps, "taxi:1:1").NeedsYou);
        Assert.Equal(TripStepStates.Waiting, Step(steps, "taxi:1:1").State);
    }

    [Fact]
    public void Own_decline_or_cancel_is_not_a_problem()
    {
        var plan = OneCity(1);
        var steps = Steps(plan, [StayRow(TripEnquiryStatuses.Declined, 1), TaxiRow(TripEnquiryStatuses.CancelledByTraveller, 1)]);
        Assert.Equal(TripStepStates.Todo, Step(steps, "stay:1:1").State);
        Assert.Equal(TripStepStates.Todo, Step(steps, "taxi:1:1").State);
    }

    [Fact]
    public void Host_problems_are_attention()
    {
        foreach (var status in new[] { TripEnquiryStatuses.NotAvailable, TripEnquiryStatuses.Expired, TripEnquiryStatuses.Failed, TripEnquiryStatuses.CancelledByOwner })
            Assert.Equal(TripStepStates.Attention, Step(Steps(OneCity(1), [StayRow(status, 1)]), "stay:1:1").State);
    }

    [Fact]
    public void A_live_ask_wins_over_an_older_problem()
        => Assert.Equal(TripStepStates.Waiting,
            Step(Steps(OneCity(1), [StayRow(TripEnquiryStatuses.Expired, 1), StayRow(TripEnquiryStatuses.Asked, 1)]), "stay:1:1").State);

    [Fact]
    public void Rest_day_is_skipped_and_counted()
    {
        var plan = OneCity(2);
        plan.Days[1].Spots.Clear();
        Assert.Equal(TripStepStates.Todo, Step(Steps(plan, []), "places:1:2").State);
        plan.Days[1].RestDay = true;
        var places = Step(Steps(plan, []), "places:1:2");
        Assert.Equal(TripStepStates.Skipped, places.State);
        Assert.True(places.Counted);
        Assert.False(places.Decided);
    }

    [Fact]
    public void Basics_need_dates_people_and_a_starting_city()
    {
        var plan = OneCity(1);
        Assert.Equal(TripStepStates.Done, TripStepProgress.BasicsState(plan));
        plan.Steps!.StartFrom = null;
        Assert.Equal(TripStepStates.Todo, TripStepProgress.BasicsState(plan));
    }

    [Fact]
    public void Journey_rows_never_count_as_a_sightseeing_taxi()
    {
        var plan = OneCity(2);
        ChooseGettingThere(plan);
        var steps = Steps(plan, [JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, GettingThereId, 1, Start, TripStepRoles.GettingThere)],
            Journey(GettingThereId, 1, Start));
        Assert.Equal(TripStepStates.Todo, Step(steps, "taxi:1:1").State);
        Assert.Equal(TripStepStates.Done, Step(steps, "gettingThere").State);
    }

    [Fact]
    public void City_change_states()
    {
        var plan = ThreeDayTwoCity();
        Assert.Equal(TripStepStates.Todo, Step(Steps(plan, []), "travel:2").State);

        var journeyId = Guid.NewGuid();
        plan.Legs![1].Arrival = new TripTravel { Mode = TripTravelModes.Journey, FromName = "Nainital", JourneyId = journeyId, Cars = 1 };
        var travel = Step(Steps(plan, [], Journey(journeyId, 1, DateOf(3))), "travel:2");
        Assert.Equal(TripStepStates.Waiting, travel.State);
        Assert.True(travel.NeedsYou);

        var moved = Step(Steps(plan, [], Journey(journeyId, 1, DateOf(2))), "travel:2");
        Assert.Equal(TripStepStates.Attention, moved.State);
    }

    [Fact]
    public void Legacy_skipped_step_is_counted_grey()
    {
        var plan = OneCity(1);
        plan.Steps!.GettingThere = new TripStep { Status = TripStepStatuses.Skipped };
        var step = Step(Steps(plan, []), "gettingThere");
        Assert.Equal(TripStepStates.Skipped, step.State);
        Assert.True(step.Counted);
    }

    [Fact]
    public void Last_event_is_the_newest_row_change()
    {
        var older = StayRow(TripEnquiryStatuses.Asked, 1);
        older.UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = TaxiRow(TripEnquiryStatuses.Asked, 1);
        newer.UpdatedAt = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(newer.UpdatedAt, Progress(OneCity(1), [older, newer]).LastEventAt);
        Assert.Null(Progress(OneCity(1), []).LastEventAt);
    }

    // ---- fix round 1: server and app agree (trip_wizard_rules_test.dart "fix round 1")

    [Fact]
    public void Only_the_newest_closed_row_can_be_a_problem()
    {
        var plan = OneCity(1);
        var old = StayRow(TripEnquiryStatuses.NotAvailable, 1);
        old.UpdatedAt = new DateTime(2026, 10, 1);
        var mine = StayRow(TripEnquiryStatuses.CancelledByTraveller, 1);
        mine.UpdatedAt = new DateTime(2026, 10, 2);
        Assert.Equal(TripStepStates.Todo, Step(Steps(plan, [old, mine]), "stay:1:1").State);
        Assert.Equal(TripStepStates.Attention, Step(Steps(plan, [old]), "stay:1:1").State);
    }

    [Fact]
    public void A_shortlist_already_asked_does_not_need_you()
    {
        var plan = OneCity(1);
        var row = StayRow(TripEnquiryStatuses.Expired, 1);
        plan.Days[0].StayShortlist = [new TripStayShortlistItem { StayId = row.ListingId, Name = "A" }];
        row.UpdatedAt = new DateTime(2026, 10, 2);
        var s = Step(Steps(plan, [row]), "stay:1:1");
        Assert.Equal((TripStepStates.Attention, false), (s.State, s.NeedsYou));
    }

    [Fact]
    public void Done_stay_needs_you_for_a_revised_price_or_a_replacing_offer()
    {
        var plan = OneCity(1);
        var both = StayRow(TripEnquiryStatuses.ConfirmedByBoth, 1);
        Assert.False(Step(Steps(plan, [both]), "stay:1:1").NeedsYou);
        var revised = TripStepProgress.Steps(TripOf(plan), plan, [both], [], new HashSet<Guid> { both.Id });
        Assert.Equal((TripStepStates.Done, true), (Step(revised, "stay:1:1").State, Step(revised, "stay:1:1").NeedsYou));
        var offer = StayRow(TripEnquiryStatuses.Offered, 1);
        var s = Step(Steps(plan, [both, offer]), "stay:1:1");
        Assert.Equal((TripStepStates.Done, true), (s.State, s.NeedsYou));
    }

    [Fact]
    public void Booked_by_you_comes_before_an_offer_or_a_fare()
    {
        var plan = OneCity(1);
        var stay = Step(Steps(plan, [StayRow(TripEnquiryStatuses.ConfirmedByTraveller, 1), StayRow(TripEnquiryStatuses.Offered, 1)]), "stay:1:1");
        Assert.Equal((TripStepStates.Waiting, false), (stay.State, stay.NeedsYou));
        var taxi = Step(Steps(plan, [TaxiRow(TripEnquiryStatuses.BookedByTraveller, 1), TaxiRow(TripEnquiryStatuses.FareSent, 1)]), "taxi:1:1");
        Assert.Equal((TripStepStates.Waiting, false), (taxi.State, taxi.NeedsYou));
    }

    [Fact]
    public void City_change_with_its_journey_missing_waits_and_needs_you()
    {
        var plan = ThreeDayTwoCity();
        plan.Legs![1].Arrival = new TripTravel { Mode = TripTravelModes.Journey, FromName = "Nainital", JourneyId = Guid.NewGuid() };
        var s = Step(Steps(plan, []), "travel:2");
        Assert.Equal((TripStepStates.Waiting, true), (s.State, s.NeedsYou));
    }

    [Fact]
    public void A_car_nobody_asked_needs_you_while_the_other_is_booked()
    {
        var plan = OneCity(1);
        ChooseGettingThere(plan);
        var journey = Journey(GettingThereId, 2, Start);
        journey.Legs.First(l => l.LegIndex == 1).LastEnquiryId = Guid.NewGuid();
        var rows = new List<TripEnquiry> { JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, GettingThereId, 1, Start, TripStepRoles.GettingThere) };
        var s = Step(Steps(plan, rows, journey), "gettingThere");
        Assert.Equal((TripStepStates.Waiting, true), (s.State, s.NeedsYou));
    }

    [Fact]
    public void A_moved_start_needs_a_fix_until_confirmed()
    {
        var plan = OneCity(1);
        ChooseGettingThere(plan);
        var journey = Journey(GettingThereId, 1, Start);
        journey.FromLatitude = 30.32m;
        journey.FromLongitude = 78.03m;
        Assert.Equal(TripStepStates.Attention, Step(Steps(plan, [], journey), "gettingThere").State);
        journey.FromLatitude = 28.61m;
        journey.FromLongitude = 77.21m;
        Assert.NotEqual(TripStepStates.Attention, Step(Steps(plan, [], journey), "gettingThere").State);
    }

    [Fact]
    public void A_car_the_traveller_cancelled_is_not_missing()
    {
        var plan = OneCity(1);
        ChooseGettingThere(plan);
        var journey = Journey(GettingThereId, 1, Start);
        journey.Legs.First().LastEnquiryId = Guid.NewGuid();
        var rows = new List<TripEnquiry> { JourneyRow(TripEnquiryStatuses.CancelledByTraveller, GettingThereId, 1, Start, TripStepRoles.GettingThere) };
        Assert.NotEqual(TripStepStates.Attention, Step(Steps(plan, rows, journey), "gettingThere").State);
    }

    // ---- fix round 2 (trip_wizard_rules_test.dart "fix round 2")

    [Fact]
    public void A_booking_of_another_city_moved_onto_a_night_is_attention_not_done()
    {
        var plan = ThreeDayTwoCity();
        var almora = StayRow(TripEnquiryStatuses.ConfirmedByBoth, 2);
        almora.ContextJson = """{"city":"Almora"}""";
        var steps = Steps(plan, [almora]);
        var night2 = Step(steps, "stay:1:2");
        Assert.Equal((TripStepStates.Attention, true), (night2.State, night2.NeedsYou));
        Assert.Equal(TripStepStates.Todo, Step(steps, "stay:2:1").State);

        var nainital = StayRow(TripEnquiryStatuses.ConfirmedByBoth, 2);
        nainital.ContextJson = """{"city":"Nainital"}""";
        Assert.Equal(TripStepStates.Done, Step(Steps(plan, [nainital]), "stay:1:2").State);
    }

    [Fact]
    public void Only_the_current_driver_can_make_a_car_missing()
    {
        var plan = OneCity(1);
        ChooseGettingThere(plan);
        var journey = Journey(GettingThereId, 1, Start);
        var leg = journey.Legs.First();
        leg.DriverTaxiId = Guid.NewGuid();
        leg.LastEnquiryId = Guid.NewGuid();
        var old = JourneyRow(TripEnquiryStatuses.NotAvailable, GettingThereId, 1, Start, TripStepRoles.GettingThere);
        old.ListingId = Guid.NewGuid();
        Assert.NotEqual(TripStepStates.Attention, Step(Steps(plan, [old], journey), "gettingThere").State);
        old.ListingId = leg.DriverTaxiId;
        Assert.Equal(TripStepStates.Attention, Step(Steps(plan, [old], journey), "gettingThere").State);
    }

    // ---- fix round 3 (trip_wizard_rules_test.dart "fix round 3")

    [Fact]
    public void Every_car_arranged_by_the_traveller_is_own()
    {
        var plan = OneCity(1);
        ChooseGettingThere(plan, [1, 2]);
        var step = Step(Steps(plan, [], Journey(GettingThereId, 2, Start)), "gettingThere");
        Assert.Equal((TripStepStates.Own, false), (step.State, step.NeedsYou));
    }

    [Fact]
    public void A_confirmed_taxi_with_another_drivers_fare_needs_you()
    {
        var plan = OneCity(1);
        var done = Step(Steps(plan, [TaxiRow(TripEnquiryStatuses.ConfirmedByBoth, 1)]), "taxi:1:1");
        Assert.Equal((TripStepStates.Done, false), (done.State, done.NeedsYou));
        var fare = Step(Steps(plan, [TaxiRow(TripEnquiryStatuses.ConfirmedByBoth, 1), TaxiRow(TripEnquiryStatuses.FareSent, 1)]), "taxi:1:1");
        Assert.Equal((TripStepStates.Done, true), (fare.State, fare.NeedsYou));
    }
}
