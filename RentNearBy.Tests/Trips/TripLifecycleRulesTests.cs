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

    [Fact]
    public void A_saved_trip_with_no_dates_is_plan_only()
    {
        var noStart = PlannedTrip();
        noStart.StartDate = null;
        Assert.Equal("planOnly", Compute(noStart, Plan(withStart: false)));
    }

    [Fact]
    public void A_dated_trip_with_no_choice_and_no_enquiry_is_plan_only()
    {
        Assert.Equal("planOnly", Compute(PlannedTrip(), Plan()));
        Assert.Equal("planOnly", Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.Closed, 1, 2)));
    }

    [Fact]
    public void Any_live_enquiry_or_choice_leaves_plan_only()
    {
        Assert.Equal("confirming", Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.Asked, 1, 1)));
        var plan = Plan();
        plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "A" };
        Assert.Equal("confirming", Compute(PlannedTrip(), plan));
    }

    [Fact]
    public void A_skipped_night_is_settled_and_counts_as_a_choice()
    {
        var plan = Plan();
        plan.Days[0].Stay = new TripStayChoice { Name = "No stay tonight", Skipped = true };
        Assert.Equal("ready", Compute(PlannedTrip(), plan));
    }

    [Fact]
    public void The_last_night_is_a_night()
    {
        var plan = Plan();
        plan.Days[2].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Last" };
        Assert.Equal("confirming", Compute(PlannedTrip(), plan));
        Assert.Equal("ready", Compute(PlannedTrip(), plan, Stay(TripEnquiryStatuses.ConfirmedByBoth, 3, 3)));
    }

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked)]
    [InlineData(TripEnquiryStatuses.Offered)]
    [InlineData(TripEnquiryStatuses.Countered)]
    public void An_open_stay_enquiry_is_confirming(string status)
        => Assert.Equal("confirming", Compute(PlannedTrip(), Plan(), Stay(status)));

    [Fact]
    public void Orange_confirmed_by_you_is_confirming()
        => Assert.Equal("confirming", Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.ConfirmedByTraveller, 1, 2)));

    [Fact]
    public void Every_night_confirmed_by_both_is_ready()
        => Assert.Equal("ready", Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)));

    [Fact]
    public void One_night_confirmed_and_the_other_open_is_confirming()
    {
        var state = Compute(PlannedTrip(), Plan(),
            Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1),
            Stay(TripEnquiryStatuses.Asked, 2, 2));
        Assert.Equal("confirming", state);
    }

    [Fact]
    public void A_red_row_without_a_settled_replacement_is_confirming()
        => Assert.Equal("confirming", Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.NotAvailable, 1, 1)));

    [Fact]
    public void A_red_row_replaced_by_a_settled_row_is_ready()
    {
        var state = Compute(PlannedTrip(), Plan(),
            Stay(TripEnquiryStatuses.NotAvailable, 1, 1),
            Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2));
        Assert.Equal("ready", state);
    }

    [Fact]
    public void Closed_rows_are_ignored()
    {
        var withClosed = Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.Closed, 1, 2));
        Assert.Equal(Compute(PlannedTrip(), Plan()), withClosed);
        var settled = Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.Closed, 1, 2), Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2));
        Assert.Equal("ready", settled);
    }

    [Fact]
    public void A_taxi_booked_by_you_settles_its_days()
    {
        var state = Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.BookedByTraveller));
        Assert.Equal("ready", state);
    }

    [Fact]
    public void A_taxi_fare_sent_but_not_booked_is_confirming()
    {
        var state = Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.FareSent));
        Assert.Equal("confirming", state);
    }

    [Fact]
    public void A_taxi_choice_without_a_booking_is_confirming_and_an_own_taxi_is_ready()
    {
        var plan = Plan();
        plan.Days[0].Taxi = new TripTaxiChoice { TaxiId = Guid.NewGuid(), Name = "Mohan" };
        Assert.Equal("confirming", Compute(PlannedTrip(), plan));

        plan.Days[0].Taxi = new TripTaxiChoice { Name = "Own", IsOwn = true };
        Assert.Equal("ready", Compute(PlannedTrip(), plan));
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
    [InlineData(TripEnquiryStatuses.ConfirmedByTraveller, TripAdvanceStatuses.None, false)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, TripAdvanceStatuses.None, false)]
    [InlineData(TripEnquiryStatuses.NotAvailable, TripAdvanceStatuses.None, false)]
    [InlineData(TripEnquiryStatuses.Closed, TripAdvanceStatuses.None, false)]
    public void IsOpenForCompletion_matrix(string status, string advance, bool expected)
        => Assert.Equal(expected, TripBookingStates.IsOpenForCompletion(Stay(status, advance: advance)));

    [Theory]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, true)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, true)]
    [InlineData(TripEnquiryStatuses.ConfirmedByTraveller, false)]
    [InlineData(TripEnquiryStatuses.Asked, false)]
    [InlineData(TripEnquiryStatuses.NotAvailable, false)]
    public void IsSettled_is_green_or_blue(string status, bool expected)
        => Assert.Equal(expected, TripBookingStates.IsSettled(Stay(status)));

    // ---- counts

    [Fact]
    public void Count_stays_taxis_and_agreed_total()
    {
        var plan = Plan();
        plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "A" };
        plan.Days[1].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "B" };
        plan.Days[0].Taxi = new TripTaxiChoice { TaxiId = Guid.NewGuid(), Name = "Mohan" };
        var stay = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1, agreed: 3800);
        var taxi = Taxi(TripEnquiryStatuses.BookedByTraveller, fare: 3400);

        var c = TripBookingSummary.Count(plan, [stay, taxi]);
        Assert.Equal(1, c.StaysConfirmed);
        Assert.Equal(2, c.StaysTotal);
        Assert.Equal(3, c.TaxisConfirmed);
        Assert.Equal(3, c.TaxisTotal);
        Assert.Equal(7200, c.AgreedTotal);
    }

    [Fact]
    public void Count_sums_each_enquiry_once_and_skips_cancelled_rows()
    {
        var stay = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2, agreed: 3800);
        var cancelled = Stay(TripEnquiryStatuses.CancelledByOwner, 1, 2, agreed: 999);
        var c = TripBookingSummary.Count(Plan(), [stay, stay, cancelled]);
        Assert.Equal(3800, c.AgreedTotal);
    }

    [Fact]
    public void Count_counts_a_confirmed_by_you_total_and_own_bookings_as_settled()
    {
        var plan = Plan();
        plan.Days[0].Stay = new TripStayChoice { Name = "Own", IsOwn = true };
        var c = TripBookingSummary.Count(plan, [Stay(TripEnquiryStatuses.ConfirmedByTraveller, 2, 2, agreed: 2000)]);
        Assert.Equal(2000, c.AgreedTotal);
        Assert.Equal(0, c.StaysConfirmed);
        Assert.Equal(1, c.StaysTotal);
        Assert.Equal(1, c.StaysOwn);
    }

    [Fact]
    public void Count_keeps_own_and_skipped_nights_and_own_taxis_out_of_the_chips()
    {
        var plan = Plan();
        plan.Days[0].Stay = new TripStayChoice { Name = "Own", IsOwn = true };
        plan.Days[1].Stay = new TripStayChoice { Name = "No stay tonight", Skipped = true };
        plan.Days[2].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "A" };
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "Own taxi", IsOwn = true };
        var c = TripBookingSummary.Count(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 3, 3, agreed: 1000)]);
        Assert.Equal(1, c.StaysConfirmed);
        Assert.Equal(1, c.StaysTotal);
        Assert.Equal(1, c.StaysOwn);
        Assert.Equal(0, c.TaxisTotal);
        Assert.Equal(1, c.TaxisOwn);
    }

    [Fact]
    public void Skipped_taxi_day_is_settled_and_kept_out_of_the_taxi_chips()
    {
        var plan = Plan();
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "No taxi this day", Skipped = true };
        plan.Days[1].Taxi = new TripTaxiChoice { TaxiId = Guid.NewGuid(), Name = "Ramesh" };
        var c = TripBookingSummary.Count(plan, []);
        Assert.Equal(0, c.TaxisConfirmed);
        Assert.Equal(1, c.TaxisTotal);
        Assert.Equal(0, c.TaxisOwn);
    }

    [Fact]
    public void All_taxi_days_skipped_with_a_settled_stay_is_ready()
    {
        var plan = Plan();
        foreach (var day in plan.Days) day.Taxi = new TripTaxiChoice { Name = "No taxi this day", Skipped = true };
        Assert.Equal("ready", Compute(PlannedTrip(), plan, Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3)));
        Assert.True(TripReminderRules.AllSettled(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3)]));
    }

    [Fact]
    public void A_booked_taxi_enquiry_on_a_skipped_day_counts_as_a_taxi()
    {
        var plan = Plan();
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "No taxi this day", Skipped = true };
        var c = TripBookingSummary.Count(plan, [Taxi(TripEnquiryStatuses.BookedByTraveller, "[1]")]);
        Assert.Equal(1, c.TaxisConfirmed);
        Assert.Equal(1, c.TaxisTotal);
    }

    [Fact]
    public void Count_of_an_empty_trip_is_zero()
    {
        var c = TripBookingSummary.Count(Plan(), []);
        Assert.Equal(new TripBookingCounts(0, 0, 0, 0, 0, 0, 0), c);
    }

    [Fact]
    public void A_taxi_row_covers_dates_so_a_moved_start_date_does_not_re_map_it()
    {
        var plan = Plan();
        plan.Settings.StartDate = Start.AddDays(1);
        var c = TripBookingSummary.Count(plan, [Taxi(TripEnquiryStatuses.BookedByTraveller)]);
        Assert.Equal(2, c.TaxisTotal);
        Assert.Equal(2, c.TaxisConfirmed);
    }

    // ---- reminder instants

    [Fact]
    public void Reminder_constants_match_the_contract()
    {
        Assert.Equal(24, TripReminderRules.NoReplyHours);
        Assert.Equal(24, TripReminderRules.HostNotConfirmedHours);
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
        Assert.Equal($"noreply:{id}", TripReminderRules.NoReplyKey(id));
        Assert.Equal($"hostnc:{id}", TripReminderRules.HostNotConfirmedKey(id));
        Assert.Equal($"d3:{id}:20261104", TripReminderRules.StartsInKey(id, new DateOnly(2026, 11, 4)));
        Assert.Equal($"d0:{id}:20261104", TripReminderRules.TodayKey(id, new DateOnly(2026, 11, 4)));
        Assert.Equal($"done:{id}:20261103", TripReminderRules.MarkCompleteKey(id, new DateOnly(2026, 11, 3)));
        Assert.NotEqual(TripReminderRules.StartsInKey(id, new DateOnly(2026, 11, 4)), TripReminderRules.StartsInKey(id, new DateOnly(2026, 11, 5)));
        Assert.True(TripReminderRules.StartsInKey(id, Start).Length <= 80);
    }

    // ---- summaries

    [Fact]
    public void StartsInSummary_samples()
    {
        var plan = Plan();
        Assert.Equal("Stay confirmed. Taxi is booked by you.",
            TripReminderRules.StartsInSummary(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.BookedByTraveller)]));
        Assert.Equal("Stay confirmed. Taxi confirmed.",
            TripReminderRules.StartsInSummary(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.ConfirmedByBoth)]));
        Assert.Equal("1 stay enquiry waiting. No taxi yet.",
            TripReminderRules.StartsInSummary(plan, [Stay(TripEnquiryStatuses.Asked, 1, 1)]));
        Assert.Equal("2 stay enquiries waiting. No taxi yet.",
            TripReminderRules.StartsInSummary(plan, [Stay(TripEnquiryStatuses.Asked, 1, 1), Stay(TripEnquiryStatuses.Offered, 2, 2)]));
        Assert.Equal("No stay yet. 1 taxi enquiry waiting.",
            TripReminderRules.StartsInSummary(plan, [Taxi(TripEnquiryStatuses.FareSent)]));
        Assert.Equal("No stay yet. No taxi yet.", TripReminderRules.StartsInSummary(plan, []));
    }

    [Fact]
    public void StartsInSummary_adds_the_getting_there_line_only_for_not_started_with_a_start()
    {
        var plan = Plan();
        var start = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 };

        plan.Steps = new TripSteps { StartFrom = start };
        Assert.Equal("No stay yet. No taxi yet. Getting there not sorted.", TripReminderRules.StartsInSummary(plan, []));

        plan.Steps = new TripSteps { StartFrom = start, GettingThere = new TripStep { Status = TripStepStatuses.NotStarted } };
        Assert.EndsWith(TripReminderRules.GettingThereNudge, TripReminderRules.StartsInSummary(plan, []));

        foreach (var status in new[] { TripStepStatuses.Chosen, TripStepStatuses.Booked, TripStepStatuses.Own, TripStepStatuses.Skipped, TripStepStatuses.NotNeeded })
        {
            plan.Steps = new TripSteps { StartFrom = start, GettingThere = new TripStep { Status = status } };
            Assert.Equal("No stay yet. No taxi yet.", TripReminderRules.StartsInSummary(plan, []));
        }

        plan.Steps = new TripSteps();
        Assert.Equal("No stay yet. No taxi yet.", TripReminderRules.StartsInSummary(plan, []));
        plan.Steps = null;
        Assert.Equal("No stay yet. No taxi yet.", TripReminderRules.StartsInSummary(plan, []));
    }

    [Fact]
    public void StartsInSummary_skipped_taxi_days_are_not_a_booked_taxi()
    {
        var plan = Plan();
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "No taxi this day", Skipped = true };
        var taxi = Taxi(TripEnquiryStatuses.ConfirmedByBoth, "[2,3]");
        taxi.CheckIn = Start.AddDays(1);
        Assert.Equal("Stay confirmed. Taxi confirmed.",
            TripReminderRules.StartsInSummary(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3), taxi]));
    }

    [Fact]
    public void TodayBody_samples()
    {
        Assert.Equal("Pickup 8:30 am. Tap for the driver's number.",
            TripReminderRules.TodayBody([Taxi(TripEnquiryStatuses.BookedByTraveller, pickup: "08:30")]));
        Assert.Equal("Tap for your bookings.", TripReminderRules.TodayBody([]));
        Assert.Equal("Tap for your bookings.", TripReminderRules.TodayBody([Taxi(TripEnquiryStatuses.BookedByTraveller)]));
        Assert.Equal("Tap for your bookings.", TripReminderRules.TodayBody([Taxi(TripEnquiryStatuses.FareSent, pickup: "08:30")]));
        Assert.Equal("Tap for your bookings.", TripReminderRules.TodayBody([Stay(TripEnquiryStatuses.ConfirmedByBoth)]));
    }

    [Fact]
    public void AllSettled_cases()
    {
        var plan = Plan();
        Assert.True(TripReminderRules.AllSettled(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.BookedByTraveller)]));
        Assert.True(TripReminderRules.AllSettled(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1)]));
        Assert.False(TripReminderRules.AllSettled(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Stay(TripEnquiryStatuses.Asked, 1, 1)]));
        Assert.False(TripReminderRules.AllSettled(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.FareSent)]));
        Assert.True(TripReminderRules.AllSettled(plan, []));

        var lastNight = Plan();
        lastNight.Days[2].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Last" };
        Assert.False(TripReminderRules.AllSettled(lastNight, []));
        Assert.True(TripReminderRules.AllSettled(lastNight, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 3, 3)]));

        var own = Plan();
        own.Days[0].Stay = new TripStayChoice { Name = "Own", IsOwn = true };
        own.Days[1].Stay = new TripStayChoice { Name = "Own", IsOwn = true };
        Assert.True(TripReminderRules.AllSettled(own, []));
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
        Assert.False(TripReminderRules.ShouldRemindStartsIn(trip, Plan(), [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)], today));
        Assert.False(TripReminderRules.ShouldRemindStartsIn(trip, Plan(), [Stay(TripEnquiryStatuses.Asked, 1, 1)], Start));
        Assert.False(TripReminderRules.ShouldRemindStartsIn(PlannedTrip(TripStatuses.Completed), Plan(), [], today));
    }

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
}
