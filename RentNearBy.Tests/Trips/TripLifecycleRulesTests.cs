namespace RentNearBy.Tests.Trips;

public class TripLifecycleRulesTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly End = new(2026, 11, 3);

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static TripPlan Plan(bool withSpots = true, bool withStart = true)
    {
        var plan = new TripPlan { Settings = new TripSettings { StartDate = withStart ? Start : null } };
        for (var d = 1; d <= 3; d++)
        {
            var day = new TripDay { Day = d };
            if (withSpots) day.Spots.Add(new TripSpot { Id = $"s{d}", Name = $"Spot {d}" });
            plan.Days.Add(day);
        }
        return plan;
    }

    private static Trip PlannedTrip(string status = TripStatuses.Planned, DateOnly? start = null, DateOnly? end = null) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        StartDate = start ?? Start,
        EndDate = end ?? End,
    };

    private static TripEnquiry Stay(string status, int fromDay = 1, int toDay = 2, int? agreed = null, string advance = TripAdvanceStatuses.None) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        Status = status,
        CheckIn = Start.AddDays(fromDay - 1),
        CheckOut = Start.AddDays(toDay),
        AgreedTotal = agreed,
        AdvanceStatus = advance,
    };

    private static TripEnquiry Taxi(string status, string days = "[1,2,3]", int? fare = null, string? pickup = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = status,
        DaysJson = days,
        CheckIn = Start,
        CheckOut = End.AddDays(1),
        AgreedFare = fare,
        PickupTime = pickup,
    };

    private static string Compute(Trip t, TripPlan p, params TripEnquiry[] e) => TripBookingStates.Compute(t, p, e);

    // ---- booking state

    [Fact]
    public void Constants_are_the_wire_values()
    {
        Assert.Equal("draft", TripBookingStates.Draft);
        Assert.Equal("planOnly", TripBookingStates.PlanOnly);
        Assert.Equal("confirming", TripBookingStates.Confirming);
        Assert.Equal("ready", TripBookingStates.Ready);
        Assert.Equal("completed", TripBookingStates.Completed);
    }

    [Fact]
    public void Completed_trip_is_completed_whatever_the_enquiries()
    {
        Assert.Equal("completed", Compute(PlannedTrip(TripStatuses.Completed), Plan(), Stay(TripEnquiryStatuses.Asked)));
    }

    [Fact]
    public void Draft_status_or_no_spots_is_draft()
    {
        Assert.Equal("draft", Compute(PlannedTrip(TripStatuses.Draft), Plan()));
        Assert.Equal("draft", Compute(PlannedTrip(), Plan(withSpots: false)));
    }
    // ---- open for completion / settled

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, TripAdvanceStatuses.None, true)]
    [InlineData(TripEnquiryStatuses.Offered, TripAdvanceStatuses.None, true)]
    [InlineData(TripEnquiryStatuses.Countered, TripAdvanceStatuses.None, true)]
    [InlineData(TripEnquiryStatuses.FareSent, TripAdvanceStatuses.None, true)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripAdvanceStatuses.Pending, true)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripAdvanceStatuses.ProofSent, true)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripAdvanceStatuses.Received, false)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripAdvanceStatuses.None, false)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, TripAdvanceStatuses.None, false)]
    [InlineData(TripEnquiryStatuses.NotAvailable, TripAdvanceStatuses.None, false)]
    [InlineData(TripEnquiryStatuses.Closed, TripAdvanceStatuses.None, false)]
    public void IsOpenForCompletion_matrix(string status, string advance, bool expected)
        => Assert.Equal(expected, TripBookingStates.IsOpenForCompletion(Stay(status, advance: advance)));

    [Theory]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, true)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, false)]
    [InlineData(TripEnquiryStatuses.Asked, false)]
    [InlineData(TripEnquiryStatuses.NotAvailable, false)]
    public void IsSettled_is_confirmed_by_both_only(string status, bool expected)
        => Assert.Equal(expected, TripBookingStates.IsSettled(Stay(status)));

    // ---- reminder instants

    [Fact]
    public void Reminder_constants_match_the_contract()
    {
        Assert.Equal(24, TripReminderRules.NoReplyHours);
        Assert.Equal(48, TripReminderRules.WindowHours);
        Assert.Equal(3, TripReminderRules.DaysBeforeStart);
        Assert.Equal(9, TripReminderRules.StartsInHourIst);
        Assert.Equal(7, TripReminderRules.TodayHourIst);
        Assert.Equal(10, TripReminderRules.MarkCompleteHourIst);
        Assert.Equal(7, TripReminderRules.AutoCompleteDays);
    }

    [Fact]
    public void Due_instants_are_ist_hours_in_utc()
    {
        var start = new DateOnly(2026, 11, 4);
        Assert.Equal(Utc(2026, 11, 1, 3, 30), TripReminderRules.StartsInDueUtc(start));
        Assert.Equal(Utc(2026, 11, 4, 1, 30), TripReminderRules.TodayDueUtc(start));
        Assert.Equal(Utc(2026, 11, 4, 4, 30), TripReminderRules.MarkCompleteDueUtc(new DateOnly(2026, 11, 3)));
    }

    [Fact]
    public void InWindow_edges()
    {
        var due = Utc(2026, 11, 1, 3, 30);
        Assert.False(TripReminderRules.InWindow(due, due.AddTicks(-1)));
        Assert.True(TripReminderRules.InWindow(due, due));
        Assert.True(TripReminderRules.InWindow(due, due.AddHours(47).AddMinutes(59)));
        Assert.False(TripReminderRules.InWindow(due, due.AddHours(48)));
        Assert.False(TripReminderRules.InWindow(due, due.AddDays(10)));
    }

    [Fact]
    public void Dedupe_keys_are_unique_per_case_and_date()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal($"rem:t:{id:N}:20261104", TripReminderRules.TravellerKey(id, new DateOnly(2026, 11, 4)));
        Assert.Equal($"d3:{id}:20261104", TripReminderRules.StartsInKey(id, new DateOnly(2026, 11, 4)));
        Assert.Equal($"d0:{id}:20261104", TripReminderRules.TodayKey(id, new DateOnly(2026, 11, 4)));
        Assert.Equal($"done:{id}:20261103", TripReminderRules.MarkCompleteKey(id, new DateOnly(2026, 11, 3)));
        Assert.NotEqual(TripReminderRules.StartsInKey(id, new DateOnly(2026, 11, 4)), TripReminderRules.StartsInKey(id, new DateOnly(2026, 11, 5)));
        Assert.True(TripReminderRules.StartsInKey(id, Start).Length <= 80);
    }

    [Fact]
    public void TodayBody_samples()
    {
        Assert.Equal("Pickup 8:30 am. Tap for the driver's number.",
            TripReminderRules.TodayBody([Taxi(TripEnquiryStatuses.BookedByTraveller, pickup: "08:30")], Start));
        Assert.Equal("Tap for your bookings.", TripReminderRules.TodayBody([], Start));
        Assert.Equal("Tap for your bookings.", TripReminderRules.TodayBody([Taxi(TripEnquiryStatuses.BookedByTraveller)], Start));
        Assert.Equal("Tap for your bookings.", TripReminderRules.TodayBody([Taxi(TripEnquiryStatuses.FareSent, pickup: "08:30")], Start));
        Assert.Equal("Tap for your bookings.", TripReminderRules.TodayBody([Stay(TripEnquiryStatuses.ConfirmedByBoth)], Start));
    }

    [Fact]
    public void TodayBody_ignores_later_taxis_and_journey_cars()
    {
        var later = Taxi(TripEnquiryStatuses.BookedByTraveller, days: "[2,3]", pickup: "07:00");
        later.CheckIn = Start.AddDays(1);
        var car = new TripEnquiry
        {
            Id = Guid.NewGuid(),
            TripId = Guid.NewGuid(),
            Kind = TripEnquiryKinds.Taxi,
            Day = 2,
            DaysJson = "[2]",
            CheckIn = Start.AddDays(1),
            CheckOut = Start.AddDays(2),
            Status = TripEnquiryStatuses.BookedByTraveller,
            PickupTime = "06:00",
            ContextJson = $$"""{"city":"Badrinath","journeyId":"{{Guid.NewGuid()}}","legIndex":1}""",
        };
        var local = Taxi(TripEnquiryStatuses.ConfirmedByBoth, days: "[1]", pickup: "09:15");

        Assert.Equal("Tap for your bookings.", TripReminderRules.TodayBody([later, car], Start));
        Assert.Equal("Pickup 9:15 am. Tap for the driver's number.", TripReminderRules.TodayBody([car, later, local], Start));
    }

    // ---- trip window rules

    [Theory]
    [InlineData(2026, 10, 31, false)]
    [InlineData(2026, 11, 1, true)]
    [InlineData(2026, 11, 3, true)]
    [InlineData(2026, 11, 4, false)]
    public void IsRunning_is_inclusive_of_start_and_end(int y, int m, int d, bool expected)
        => Assert.Equal(expected, TripReminderRules.IsRunning(PlannedTrip(), new DateOnly(y, m, d)));

    [Theory]
    [InlineData(2026, 10, 31, false)]
    [InlineData(2026, 11, 1, true)]
    [InlineData(2026, 11, 3, true)]
    [InlineData(2026, 11, 20, true)]
    public void CanComplete_from_the_first_day(int y, int m, int d, bool expected)
        => Assert.Equal(expected, TripReminderRules.CanComplete(PlannedTrip(), new DateOnly(y, m, d)));

    [Fact]
    public void CanComplete_a_trip_with_no_dates_any_day()
    {
        var t = PlannedTrip();
        t.StartDate = null;
        t.EndDate = null;
        Assert.True(TripReminderRules.CanComplete(t, new DateOnly(2020, 1, 1)));
        t.StartDate = Start;
        Assert.True(TripReminderRules.CanComplete(t, new DateOnly(2026, 12, 1)));
    }

    [Theory]
    [InlineData(2026, 10, 29, 3)]
    [InlineData(2026, 10, 31, 1)]
    [InlineData(2026, 11, 1, 0)]
    [InlineData(2026, 11, 3, -2)]
    public void DaysLeft_is_the_real_gap(int y, int m, int d, int expected)
        => Assert.Equal(expected, TripReminderRules.DaysLeft(Start, new DateOnly(y, m, d)));

    [Theory]
    [InlineData(2026, 10, 31, false)]
    [InlineData(2026, 11, 1, true)]
    [InlineData(2026, 11, 3, true)]
    [InlineData(2026, 11, 4, true)]
    [InlineData(2026, 11, 5, false)]
    public void ProblemAllowed_runs_to_end_plus_one(int y, int m, int d, bool expected)
        => Assert.Equal(expected, TripReminderRules.ProblemAllowed(PlannedTrip(), new DateOnly(y, m, d)));

    [Theory]
    [InlineData(2026, 11, 9, false)]
    [InlineData(2026, 11, 10, true)]
    [InlineData(2026, 12, 1, true)]
    public void AutoCompleteDue_seven_days_after_the_end(int y, int m, int d, bool expected)
        => Assert.Equal(expected, TripReminderRules.AutoCompleteDue(End, new DateOnly(y, m, d)));

    // ---- problem kinds

    [Fact]
    public void Problem_kinds_map_to_fail_reasons_and_expected_kinds()
    {
        Assert.Equal(4, TripProblemKinds.All.Length);
        Assert.Equal(TripFailReasons.ProblemDriverNoShow, TripProblemKinds.FailReasonOf(TripProblemKinds.DriverNoShow));
        Assert.Equal(TripFailReasons.ProblemHostNoRoom, TripProblemKinds.FailReasonOf(TripProblemKinds.HostNoRoom));
        Assert.Null(TripProblemKinds.FailReasonOf(TripProblemKinds.RoadClosed));
        Assert.Null(TripProblemKinds.FailReasonOf(TripProblemKinds.Other));
        Assert.True(TripProblemKinds.MarksBooking(TripProblemKinds.DriverNoShow));
        Assert.True(TripProblemKinds.MarksBooking(TripProblemKinds.HostNoRoom));
        Assert.False(TripProblemKinds.MarksBooking(TripProblemKinds.RoadClosed));
        Assert.False(TripProblemKinds.MarksBooking(TripProblemKinds.Other));
        Assert.Equal(TripEnquiryKinds.Taxi, TripProblemKinds.ExpectedKind(TripProblemKinds.DriverNoShow));
        Assert.Equal(TripEnquiryKinds.Stay, TripProblemKinds.ExpectedKind(TripProblemKinds.HostNoRoom));
    }

    [Fact]
    public void Trip_statuses_keep_completed_out_of_the_savable_set()
    {
        Assert.Contains(TripStatuses.Completed, TripStatuses.All);
        Assert.DoesNotContain(TripStatuses.Completed, TripStatuses.Savable);
        Assert.Contains(TripStatuses.Draft, TripStatuses.Savable);
        Assert.Contains(TripStatuses.Planned, TripStatuses.Savable);
    }

    // ---- travel between cities (CONTRACT_MCB D6): Haridwar days 1-2, Badrinath days 3-4, day 3 = travel day

    private static readonly Guid JourneyA = Guid.Parse("dddddddd-0000-0000-0000-00000000000a");
    private static readonly Guid JourneyB = Guid.Parse("dddddddd-0000-0000-0000-00000000000b");

    private static TripPlan TwoCityPlan(TripTravel? arrival)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs =
            [
                new TripCityLeg { Index = 1, CityName = "Haridwar" },
                new TripCityLeg { Index = 2, CityName = "Badrinath", Arrival = arrival },
            ],
        };
        for (var d = 1; d <= 4; d++)
        {
            var day = new TripDay { Day = d, Leg = d <= 2 ? 1 : 2 };
            day.Spots.Add(new TripSpot { Id = $"s{d}", Name = $"Spot {d}" });
            plan.Days.Add(day);
        }
        return plan;
    }

    private static TripTravel JourneyArrival(Guid id, int cars = 2) =>
        new() { FromName = "Haridwar", JourneyId = id, Cars = cars };

    private static TripEnquiry Segment(string status, int car, Guid? journey = null, int? fare = null) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = status,
        Day = 3,
        DaysJson = "[3]",
        CheckIn = Start.AddDays(2),
        CheckOut = Start.AddDays(3),
        AgreedFare = fare,
        ContextJson = $$"""{"city":"Badrinath","journeyId":"{{journey ?? JourneyA}}","legIndex":{{car}}}""",
    };

    private static Trip FourDayTrip() => PlannedTrip(end: Start.AddDays(3));

    // A saved journey whose cars were all asked (answer Free), travelling on the given date (default: day 3).
    private static SavedJourney Saved(Guid id, int cars = 2, DateOnly? date = null)
    {
        var journey = new SavedJourney { Id = id, TravelDate = date ?? Start.AddDays(2), Cars = (short)cars };
        for (var car = 1; car <= cars; car++)
            journey.Legs.Add(new SavedJourneyLeg
            {
                LegIndex = (short)car, LastEnquiryId = Guid.NewGuid(), LastKnownStatus = JourneyLegStatuses.Free, DriverAvailable = true,
            });
        return journey;
    }

    private static SavedJourney SavedA(int cars = 2) => Saved(JourneyA, cars);

    [Fact]
    public void Morning_reminder_keys_are_short_and_keyed_by_person_and_date()
    {
        var user = Guid.NewGuid();
        var key = TripReminderRules.TravellerKey(user, new DateOnly(2026, 11, 3));
        Assert.StartsWith("rem:t:", key);
        Assert.EndsWith(":20261103", key);
        Assert.Contains(user.ToString("N"), key);
        Assert.True(key.Length <= 80);
        Assert.NotEqual(TripReminderRules.HostKey(user, new DateOnly(2026, 11, 3)), key);
    }

    // ---- travel-eve reminder and route titles (CONTRACT_MCD D3, D4)

    [Fact]
    public void TravelEveDueUtc_is_18_ist_of_the_eve()
    {
        Assert.Equal(18, TripReminderRules.TravelEveHourIst);
        Assert.Equal(Utc(2026, 11, 2, 12, 30), TripReminderRules.TravelEveDueUtc(new DateOnly(2026, 11, 3)));
    }

    [Fact]
    public void TravelEveInWindow_is_the_eve_only()
    {
        var travel = new DateOnly(2026, 11, 3);
        Assert.True(TripReminderRules.TravelEveInWindow(travel, Utc(2026, 11, 2, 12, 30)));
        Assert.False(TripReminderRules.TravelEveInWindow(travel, Utc(2026, 11, 2, 12, 29)));
        Assert.True(TripReminderRules.TravelEveInWindow(travel, Utc(2026, 11, 2, 18, 29)));
        Assert.False(TripReminderRules.TravelEveInWindow(travel, Utc(2026, 11, 2, 18, 30)));
        Assert.False(TripReminderRules.TravelEveInWindow(travel, Utc(2026, 11, 4, 12, 30)));
    }

    [Fact]
    public void TravelEveKey_format_and_length()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var key = TripReminderRules.TravelEveKey(id, new DateOnly(2026, 11, 3));
        Assert.Equal($"trv:{id}:20261103", key);
        Assert.True(key.Length <= 80);
        Assert.NotEqual(key, TripReminderRules.TravelEveKey(id, new DateOnly(2026, 11, 4)));
        Assert.NotEqual(TripReminderRules.StartsInKey(id, new DateOnly(2026, 11, 3)), key);
    }

    [Fact]
    public void RouteTitle_one_city_two_cities_and_cut()
    {
        Assert.Equal("Haridwar", TripReminderRules.RouteTitle(["Haridwar"]));
        Assert.Equal("Haridwar → Badrinath", TripReminderRules.RouteTitle(["Haridwar", "Badrinath"]));
        var cut = TripReminderRules.RouteTitle(["Haridwar", "Rishikesh", "Joshimath", "Badrinath"]);
        Assert.True(cut.Length <= 40);
        Assert.EndsWith("…", cut);
        Assert.StartsWith("Haridwar → Rishikesh", cut);
    }

    // ---- step journeys in the booking map (keyed by role, leg, car)

    private static readonly Guid StepJourney = Guid.Parse("dddddddd-0000-0000-0000-0000000000c1");

    private static TripEnquiry StepRow(string status, int car, string role, DateOnly date) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = status,
        Day = TripExecutionRules.DayOf(Start, date),
        DaysJson = $"[{TripExecutionRules.DayOf(Start, date)}]",
        CheckIn = date,
        CheckOut = date.AddDays(1),
        ContextJson = $$"""{"city":"Nainital","journeyId":"{{StepJourney}}","legIndex":{{car}},"role":"{{role}}"}""",
    };

    private static TripPlan WithGettingThereJourney(List<int>? ownCars = null)
    {
        var plan = Plan();
        plan.Steps = new TripSteps
        {
            StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 },
            GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = StepJourney, OwnCars = ownCars },
        };
        return plan;
    }

    // ---- My Trips active / past (IST day)

    private static readonly DateOnly Today = new(2026, 11, 10);

    [Theory]
    [InlineData(TripStatuses.Planned, 0, false)]
    [InlineData(TripStatuses.Planned, -1, false)]
    [InlineData(TripStatuses.Planned, -2, true)]
    [InlineData(TripStatuses.Draft, 5, false)]
    [InlineData(TripStatuses.Completed, 5, true)]
    public void IsPast_keeps_the_check_out_day_active(string status, int endOffset, bool past)
        => Assert.Equal(past, TripListRules.IsPast(status, Today.AddDays(endOffset), Today));

    [Fact]
    public void IsPast_without_dates_is_active_until_completed()
    {
        Assert.False(TripListRules.IsPast(TripStatuses.Planned, null, Today));
        Assert.True(TripListRules.IsPast(TripStatuses.Completed, null, Today));
    }

    [Fact]
    public void List_predicates_split_every_trip_once_like_IsPast()
    {
        var trips = new[]
        {
            new Trip { Status = TripStatuses.Planned, EndDate = Today.AddDays(-1) },
            new Trip { Status = TripStatuses.Planned, EndDate = Today.AddDays(-2) },
            new Trip { Status = TripStatuses.Completed, EndDate = Today.AddDays(3) },
            new Trip { Status = TripStatuses.Draft, EndDate = null },
        };
        var past = TripListRules.PastPredicate(Today).Compile();
        var active = TripListRules.ActivePredicate(Today).Compile();
        foreach (var t in trips)
        {
            Assert.Equal(TripListRules.IsPast(t.Status, t.EndDate, Today), past(t));
            Assert.NotEqual(past(t), active(t));
        }
        Assert.True(active(trips[0]));
        Assert.True(past(trips[1]));
        Assert.True(past(trips[2]));
        Assert.True(active(trips[3]));
    }

    [Theory]
    [InlineData(null, TripListRules.Active)]
    [InlineData("active", TripListRules.Active)]
    [InlineData("upcoming", TripListRules.Active)]
    [InlineData("PAST", TripListRules.Past)]
    [InlineData("completed", TripListRules.Past)]
    [InlineData("later", null)]
    public void List_state_accepts_the_old_names(string? state, string? expected)
        => Assert.Equal(expected, TripListRules.Resolve(state));

    // ---- reminders and summaries on the Make it easy rows

    private static TripPlan AllOwnPlan(bool gettingThereOwn = true)
    {
        var plan = Plan();
        plan.Legs = [new TripCityLeg { Index = 1, CityName = "Haridwar", StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own } }];
        plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        plan.Steps = new TripSteps
        {
            StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 },
            GettingThere = gettingThereOwn ? new TripStep { Status = TripStepStatuses.Own } : null,
            ComingBack = new TripStep { Status = TripStepStatuses.Own },
        };
        return plan;
    }

    private static TripPlan WithGettingThere(string status)
    {
        var plan = AllOwnPlan();
        plan.Steps!.GettingThere = new TripStep { Status = status };
        return plan;
    }

    private static List<TripEasyRow> RowsOf(TripPlan plan, params TripEnquiry[] rows) => TripEasyRows.Build(PlannedTrip(), plan, rows, []);

    [Fact]
    public void AllSettled_is_every_row_that_is_not_skipped_done()
    {
        Assert.True(TripReminderRules.AllSettled(RowsOf(AllOwnPlan())));
        Assert.False(TripReminderRules.AllSettled(RowsOf(Plan())));

        var plan = AllOwnPlan();
        plan.Legs![0].StayMark = null;
        Assert.False(TripReminderRules.AllSettled(RowsOf(plan, Stay(TripEnquiryStatuses.Asked, 1, 1))));
        plan.TaxiMark = null;
        Assert.False(TripReminderRules.AllSettled(RowsOf(plan, Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1), Taxi(TripEnquiryStatuses.FareSent))));
        plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
        Assert.True(TripReminderRules.AllSettled(RowsOf(plan, Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3))));
        Assert.False(TripReminderRules.AllSettled(RowsOf(plan, Stay(TripEnquiryStatuses.Offered, 1, 3))));
    }

    [Fact]
    public void Starts_in_reminder_rules()
    {
        var today = Start.AddDays(-3);
        var trip = PlannedTrip();
        trip.DistrictId = null;
        Assert.False(TripReminderRules.ShouldRemindStartsIn(trip, Plan(), [], today));
        trip.DistrictId = Guid.NewGuid();
        Assert.True(TripReminderRules.ShouldRemindStartsIn(trip, Plan(), [], today));
        Assert.True(TripReminderRules.ShouldRemindStartsIn(trip, Plan(), [Stay(TripEnquiryStatuses.Asked, 1, 1)], today));
        Assert.False(TripReminderRules.ShouldRemindStartsIn(trip, AllOwnPlan(), [], today));
        Assert.False(TripReminderRules.ShouldRemindStartsIn(trip, Plan(), [Stay(TripEnquiryStatuses.Asked, 1, 1)], Start));
        Assert.False(TripReminderRules.ShouldRemindStartsIn(PlannedTrip(TripStatuses.Completed), Plan(), [], today));
    }

    [Fact]
    public void Starts_in_sends_the_nudge_and_names_it_last()
    {
        var today = Start.AddDays(-3);
        var trip = PlannedTrip();
        var plan = WithGettingThere(TripStepStatuses.NotStarted);

        Assert.True(TripReminderRules.ShouldSendStartsIn(trip, plan, [], today));
        Assert.Equal("Stay confirmed. Taxi confirmed. Getting there not sorted.", TripReminderRules.StartsInBody(trip, plan, [], today));

        var open = Plan();
        open.Steps = new TripSteps { StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 } };
        Assert.Equal("Stay confirmed. Taxi confirmed. Getting there not sorted.", TripReminderRules.StartsInSummary(
            trip, open, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3), Taxi(TripEnquiryStatuses.ConfirmedByBoth)]));
    }

    [Fact]
    public void Starts_in_sends_the_nudge_alone_without_a_district()
    {
        var today = Start.AddDays(-3);
        var trip = PlannedTrip();
        trip.DistrictId = null;
        var plan = Plan();
        plan.Steps = new TripSteps { StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 } };

        Assert.True(TripReminderRules.ShouldSendStartsIn(trip, plan, [], today));
        Assert.Equal(TripReminderRules.GettingThereNudge, TripReminderRules.StartsInBody(trip, plan, [], today));
    }

    [Theory]
    [InlineData(TripStepStatuses.Own)]
    [InlineData(TripStepStatuses.Skipped)]
    public void Starts_in_no_nudge_when_getting_there_is_handled(string status)
        => Assert.False(TripReminderRules.ShouldSendStartsIn(PlannedTrip(), WithGettingThere(status), [], Start.AddDays(-3)));

    [Fact]
    public void Starts_in_nudge_keeps_the_days_left_guard()
        => Assert.False(TripReminderRules.ShouldSendStartsIn(PlannedTrip(), WithGettingThere(TripStepStatuses.NotStarted), [], Start));

    [Fact]
    public void StartsInSummary_samples()
    {
        var trip = PlannedTrip();
        var plan = Plan();
        Assert.Equal("Stay confirmed. Taxi confirmed.",
            TripReminderRules.StartsInSummary(trip, plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3), Taxi(TripEnquiryStatuses.BookedByTraveller)]));
        Assert.Equal("Stay confirmed. Taxi confirmed.",
            TripReminderRules.StartsInSummary(trip, plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3), Taxi(TripEnquiryStatuses.ConfirmedByBoth)]));
        Assert.Equal("1 stay enquiry waiting. No taxi yet.", TripReminderRules.StartsInSummary(trip, plan, [Stay(TripEnquiryStatuses.Asked, 1, 1)]));
        Assert.Equal("2 stay enquiries waiting. No taxi yet.",
            TripReminderRules.StartsInSummary(trip, plan, [Stay(TripEnquiryStatuses.Asked, 1, 1), Stay(TripEnquiryStatuses.Offered, 2, 2)]));
        Assert.Equal("No stay yet. 1 taxi enquiry waiting.", TripReminderRules.StartsInSummary(trip, plan, [Taxi(TripEnquiryStatuses.FareSent)]));
        Assert.Equal("No stay yet. No taxi yet.", TripReminderRules.StartsInSummary(trip, plan, []));
    }

    [Fact]
    public void StartsInSummary_says_nothing_about_a_skipped_row_and_an_own_row_is_settled()
    {
        var plan = Plan();
        plan.Legs = [new TripCityLeg { Index = 1, CityName = "Haridwar", StayMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped } }];
        plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        Assert.Equal("Taxi confirmed.", TripReminderRules.StartsInSummary(PlannedTrip(), plan, []));
    }

    [Fact]
    public void StartsInSummary_two_cities_travel_sentences()
    {
        var own = TwoCityPlan(null);
        own.Legs![1].TravelMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        Assert.Equal("No stay yet. No taxi yet. Travel booked.", TripReminderRules.StartsInSummary(FourDayTrip(), own, []));

        var open = TwoCityPlan(JourneyArrival(JourneyA));
        Assert.Equal("No stay yet. No taxi yet. 1 travel not booked.",
            TripReminderRules.StartsInSummary(FourDayTrip(), open, [], [SavedA()]));

        Assert.Equal("No stay yet. No taxi yet.", TripReminderRules.StartsInSummary(FourDayTrip(), TwoCityPlan(null), []));
    }
}
