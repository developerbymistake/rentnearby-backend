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

    // Ready needs every step counted: basics, Getting there and Coming back own, and every night / taxi day that no
    // trip row covers sorted as own stay / no taxi. The scenario under test keeps its own choices and rows.
    private static TripPlan Full(TripPlan plan, params TripEnquiry[] rows)
    {
        plan.Settings.Adults ??= 2;
        plan.Steps ??= new TripSteps();
        plan.Steps.StartFrom ??= new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 };
        plan.Steps.GettingThere ??= new TripStep { Status = TripStepStatuses.Own };
        plan.Steps.ComingBack ??= new TripStep { Status = TripStepStatuses.Own };
        foreach (var leg in (plan.Legs ?? []).Skip(1).Where(l => l.Arrival == null))
            leg.Arrival = new TripTravel { Mode = TripTravelModes.Own, FromName = "Haridwar", How = "bus" };
        var stays = Covered(plan, TripEnquiryKinds.Stay, rows);
        var taxis = Covered(plan, TripEnquiryKinds.Taxi, rows);
        foreach (var day in plan.Days)
        {
            if (day.Stay == null && !stays.Contains(day.Day)) day.Stay = new TripStayChoice { Name = "Own", IsOwn = true };
            if (day.Taxi == null && !taxis.Contains(day.Day)) day.Taxi = new TripTaxiChoice { Name = "No taxi this day", Skipped = true };
        }
        return plan;
    }

    private static HashSet<int> Covered(TripPlan plan, string kind, TripEnquiry[] rows)
        => plan.Settings.StartDate is not { } start
            ? new HashSet<int>()
            : rows.Where(r => r.Kind == kind && r.Status != TripEnquiryStatuses.Closed && !TripSegmentRows.IsSegment(r))
                .SelectMany(TripLegRules.DatesOf)
                .Select(d => d.DayNumber - start.DayNumber + 1)
                .ToHashSet();

    private static string ComputeFull(Trip t, TripPlan p, params TripEnquiry[] e) => Compute(t, Full(p, e), e);

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
        Assert.Equal("ready", ComputeFull(PlannedTrip(), plan));
    }

    [Fact]
    public void Ready_needs_every_step_counted()
    {
        TripEnquiry[] rows = [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3)];
        Assert.Equal("confirming", Compute(PlannedTrip(), Plan(), rows));
        Assert.Equal("ready", ComputeFull(PlannedTrip(), Plan(), rows));

        var noComingBack = Full(Plan(), rows);
        noComingBack.Steps!.ComingBack = null;
        Assert.Equal("confirming", Compute(PlannedTrip(), noComingBack, rows));
    }

    [Fact]
    public void One_confirmed_night_of_three_is_confirming()
    {
        var plan = Plan();
        plan.Days[1].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "B" };
        plan.Days[2].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "C" };
        Assert.Equal("confirming", ComputeFull(PlannedTrip(), plan, Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1)));
    }

    [Fact]
    public void The_last_night_is_a_night()
    {
        var plan = Plan();
        plan.Days[2].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Last" };
        Assert.Equal("confirming", Compute(PlannedTrip(), plan));
        Assert.Equal("ready", ComputeFull(PlannedTrip(), plan, Stay(TripEnquiryStatuses.ConfirmedByBoth, 3, 3)));
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
        => Assert.Equal("ready", ComputeFull(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)));

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
        var state = ComputeFull(PlannedTrip(), Plan(),
            Stay(TripEnquiryStatuses.NotAvailable, 1, 1),
            Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2));
        Assert.Equal("ready", state);
    }

    [Fact]
    public void Closed_rows_are_ignored()
    {
        var withClosed = Compute(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.Closed, 1, 2));
        Assert.Equal(Compute(PlannedTrip(), Plan()), withClosed);
        var settled = ComputeFull(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.Closed, 1, 2), Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2));
        Assert.Equal("ready", settled);
    }

    [Fact]
    public void A_taxi_booked_by_you_waits_for_the_driver()
    {
        var booked = ComputeFull(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.BookedByTraveller));
        Assert.Equal("confirming", booked);
        var confirmed = ComputeFull(PlannedTrip(), Plan(), Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.ConfirmedByBoth));
        Assert.Equal("ready", confirmed);
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
        Assert.Equal("ready", ComputeFull(PlannedTrip(), plan));
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
    [InlineData(TripEnquiryStatuses.BookedByTraveller, false)]
    [InlineData(TripEnquiryStatuses.ConfirmedByTraveller, false)]
    [InlineData(TripEnquiryStatuses.Asked, false)]
    [InlineData(TripEnquiryStatuses.NotAvailable, false)]
    public void IsSettled_is_confirmed_by_both_only(string status, bool expected)
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
        // Booked by the traveller alone is not confirmed; the agreed money still adds up.
        Assert.Equal(0, c.TaxisConfirmed);
        Assert.Equal(3, c.TaxisTotal);
        Assert.Equal(7200, c.AgreedTotal);

        var confirmed = TripBookingSummary.Count(plan, [stay, Taxi(TripEnquiryStatuses.ConfirmedByBoth, fare: 3400)]);
        Assert.Equal(3, confirmed.TaxisConfirmed);
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
        Assert.Equal("ready", ComputeFull(PlannedTrip(), plan, Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3)));
        Assert.True(TripReminderRules.AllSettled(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3)]));
    }

    [Fact]
    public void A_confirmed_taxi_enquiry_on_a_skipped_day_counts_as_a_taxi()
    {
        var plan = Plan();
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "No taxi this day", Skipped = true };
        var c = TripBookingSummary.Count(plan, [Taxi(TripEnquiryStatuses.ConfirmedByBoth, "[1]")]);
        Assert.Equal(1, c.TaxisConfirmed);
        Assert.Equal(1, c.TaxisTotal);
    }

    [Fact]
    public void A_taxi_booked_by_you_on_a_skipped_day_is_two_plans_not_a_confirmed_taxi()
    {
        var plan = Plan();
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "No taxi this day", Skipped = true };
        TripEnquiry[] rows = [Taxi(TripEnquiryStatuses.BookedByTraveller, "[1]")];
        var c = TripBookingSummary.Count(plan, rows);
        Assert.Equal(0, c.TaxisConfirmed);
        Assert.Equal("confirming", ComputeFull(PlannedTrip(), plan, rows));
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
        var c = TripBookingSummary.Count(plan, [Taxi(TripEnquiryStatuses.ConfirmedByBoth)]);
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

    [Fact]
    public void AllSettled_cases()
    {
        var plan = Plan();
        Assert.False(TripReminderRules.AllSettled(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.BookedByTraveller)]));
        Assert.True(TripReminderRules.AllSettled(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2), Taxi(TripEnquiryStatuses.ConfirmedByBoth)]));
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

    private static TripPlan WithGettingThere(string status)
    {
        var plan = Plan();
        plan.Steps = new TripSteps
        {
            StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 },
            GettingThere = new TripStep { Status = status },
        };
        return plan;
    }

    [Fact]
    public void Starts_in_sends_the_nudge_for_a_ready_trip()
    {
        var today = Start.AddDays(-3);
        var trip = PlannedTrip();
        TripEnquiry[] ready = [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)];
        var plan = WithGettingThere(TripStepStatuses.NotStarted);

        Assert.False(TripReminderRules.ShouldRemindStartsIn(trip, plan, ready, today));
        Assert.True(TripReminderRules.ShouldSendStartsIn(trip, plan, ready, today));
        Assert.Equal(TripReminderRules.GettingThereNudge, TripReminderRules.StartsInBody(trip, plan, ready, today));
    }

    [Fact]
    public void Starts_in_sends_the_nudge_without_a_district()
    {
        var today = Start.AddDays(-3);
        var trip = PlannedTrip();
        trip.DistrictId = null;
        var plan = WithGettingThere(TripStepStatuses.NotStarted);

        Assert.True(TripReminderRules.ShouldSendStartsIn(trip, plan, [], today));
        Assert.Equal("Getting there not sorted.", TripReminderRules.StartsInBody(trip, plan, [], today));
    }

    [Theory]
    [InlineData(TripStepStatuses.Own)]
    [InlineData(TripStepStatuses.Skipped)]
    [InlineData(TripStepStatuses.NotNeeded)]
    public void Starts_in_no_nudge_when_getting_there_is_handled(string status)
    {
        var today = Start.AddDays(-3);
        TripEnquiry[] ready = [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)];

        Assert.False(TripReminderRules.ShouldSendStartsIn(PlannedTrip(), WithGettingThere(status), ready, today));
    }

    [Fact]
    public void Starts_in_nudge_keeps_the_days_left_guard()
    {
        TripEnquiry[] ready = [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)];

        Assert.False(TripReminderRules.ShouldSendStartsIn(PlannedTrip(), WithGettingThere(TripStepStatuses.NotStarted), ready, Start));
    }

    [Fact]
    public void Starts_in_booking_reminder_keeps_the_summary_with_the_nudge()
    {
        var today = Start.AddDays(-3);
        var trip = PlannedTrip();
        TripEnquiry[] open = [Stay(TripEnquiryStatuses.Asked, 1, 1)];
        var plan = WithGettingThere(TripStepStatuses.NotStarted);

        Assert.True(TripReminderRules.ShouldSendStartsIn(trip, plan, open, today));
        Assert.Equal(TripReminderRules.StartsInSummary(plan, open), TripReminderRules.StartsInBody(trip, plan, open, today));
        Assert.EndsWith(TripReminderRules.GettingThereNudge, TripReminderRules.StartsInBody(trip, plan, open, today));
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
        new() { Mode = TripTravelModes.Journey, FromName = "Haridwar", JourneyId = id, Cars = cars };

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

    private static string ComputeWith(Trip t, TripPlan p, SavedJourney[] journeys, params TripEnquiry[] e)
        => TripBookingStates.Compute(t, Full(p, e), e, journeys);

    [Fact]
    public void Segment_rows_stay_out_of_taxi_counts_but_in_the_agreed_total()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA));
        var c = TripBookingSummary.Count(plan, [Segment(TripEnquiryStatuses.ConfirmedByBoth, 1, fare: 4200)]);
        Assert.Equal(0, c.TaxisTotal);
        Assert.Equal(0, c.TaxisConfirmed);
        Assert.Equal(2, c.TravelTotal);
        Assert.Equal(1, c.TravelConfirmed);
        Assert.Equal(4200, c.AgreedTotal);

        var booked = TripBookingSummary.Count(plan, [Segment(TripEnquiryStatuses.BookedByTraveller, 1, fare: 4200)]);
        Assert.Equal(0, booked.TravelConfirmed);
        Assert.Equal(4200, booked.AgreedTotal);
    }

    [Fact]
    public void Two_cars_need_both_drivers_to_confirm()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA));
        Assert.Equal(TripBookingStates.Confirming, ComputeFull(FourDayTrip(), plan, Segment(TripEnquiryStatuses.ConfirmedByBoth, 1)));
        Assert.Equal(TripBookingStates.Confirming, ComputeFull(FourDayTrip(), plan,
            Segment(TripEnquiryStatuses.BookedByTraveller, 1), Segment(TripEnquiryStatuses.ConfirmedByBoth, 2)));
        Assert.Equal(TripBookingStates.Ready, ComputeWith(FourDayTrip(), plan, [SavedA()],
            Segment(TripEnquiryStatuses.ConfirmedByBoth, 1), Segment(TripEnquiryStatuses.ConfirmedByBoth, 2)));
    }

    [Fact]
    public void An_own_car_is_settled()
    {
        var arrival = JourneyArrival(JourneyA);
        arrival.OwnCars = [2];
        var plan = TwoCityPlan(arrival);
        TripEnquiry[] rows = [Segment(TripEnquiryStatuses.ConfirmedByBoth, 1)];
        Assert.Equal(TripBookingStates.Ready, ComputeWith(FourDayTrip(), plan, [SavedA()], rows));
        Assert.True(TripReminderRules.AllSettled(plan, rows));
        Assert.Equal(2, TripBookingSummary.Count(plan, rows).TravelConfirmed);
    }

    [Fact]
    public void Saved_unasked_journey_keeps_the_trip_confirming()
        => Assert.Equal(TripBookingStates.Confirming, Compute(FourDayTrip(), TwoCityPlan(JourneyArrival(JourneyA))));

    [Fact]
    public void Own_travel_is_settled_and_not_a_travel_car()
    {
        var plan = TwoCityPlan(new TripTravel { Mode = TripTravelModes.Own, FromName = "Haridwar", How = "bus" });
        Assert.True(TripReminderRules.AllSettled(plan, []));
        var c = TripBookingSummary.Count(plan, []);
        Assert.Equal(0, c.TravelTotal);
        Assert.Equal(0, c.TaxisTotal);

        plan.Days[0].Stay = new TripStayChoice { Name = "Own", IsOwn = true };
        Assert.Equal(TripBookingStates.Ready, ComputeFull(FourDayTrip(), plan));
    }

    [Fact]
    public void Own_travel_alone_keeps_the_trip_plan_only_and_the_3_day_push_on()
    {
        var plan = TwoCityPlan(new TripTravel { Mode = TripTravelModes.Own, FromName = "Haridwar", How = "bus" });
        var trip = FourDayTrip();
        trip.DistrictId = Guid.NewGuid();
        Assert.Equal(TripBookingStates.PlanOnly, Compute(trip, plan));
        Assert.True(TripReminderRules.ShouldRemindStartsIn(trip, plan, [], Start.AddDays(-3)));
    }

    [Fact]
    public void Journey_cars_booked_on_an_old_travel_date_do_not_settle_the_trip()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA));
        // A day added to city 1: the travel day is now day 4, the bookings still carry day 3's date.
        plan.Days[2].Leg = 1;
        var oldDate = new[] { Segment(TripEnquiryStatuses.BookedByTraveller, 1), Segment(TripEnquiryStatuses.ConfirmedByBoth, 2) };
        foreach (var row in oldDate) row.Day = 4;
        Assert.Equal(TripBookingStates.Confirming, Compute(FourDayTrip(), plan, oldDate));
        Assert.False(TripReminderRules.AllSettled(plan, oldDate));
        Assert.Equal(0, TripBookingSummary.Count(plan, oldDate).TravelConfirmed);

        foreach (var row in oldDate)
        {
            row.Status = TripEnquiryStatuses.ConfirmedByBoth;
            row.CheckIn = Start.AddDays(3);
            row.CheckOut = Start.AddDays(4);
        }
        Assert.Equal(TripBookingStates.Ready, ComputeWith(FourDayTrip(), plan, [Saved(JourneyA, date: Start.AddDays(3))], oldDate));
    }

    [Fact]
    public void No_arrival_blocks_nothing()
        => Assert.Equal(TripBookingStates.PlanOnly, Compute(FourDayTrip(), TwoCityPlan(null)));

    [Fact]
    public void Stale_segment_rows_of_a_replaced_journey_are_ignored()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA));
        Assert.Equal(TripBookingStates.Ready, ComputeWith(FourDayTrip(), plan, [SavedA()],
            Segment(TripEnquiryStatuses.ConfirmedByBoth, 1), Segment(TripEnquiryStatuses.ConfirmedByBoth, 2),
            Segment(TripEnquiryStatuses.Asked, 1, JourneyB), Segment(TripEnquiryStatuses.NotAvailable, 2, JourneyB)));
    }

    [Fact]
    public void Red_segment_row_counts_until_another_driver_confirms_that_car()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA, cars: 1));
        Assert.Equal(TripBookingStates.Confirming, Compute(FourDayTrip(), plan, Segment(TripEnquiryStatuses.NotAvailable, 1)));
        Assert.Equal(TripBookingStates.Confirming, ComputeWith(FourDayTrip(), plan, [SavedA(1)],
            Segment(TripEnquiryStatuses.NotAvailable, 1), Segment(TripEnquiryStatuses.BookedByTraveller, 1)));
        Assert.Equal(TripBookingStates.Ready, ComputeWith(FourDayTrip(), plan, [SavedA(1)],
            Segment(TripEnquiryStatuses.NotAvailable, 1), Segment(TripEnquiryStatuses.ConfirmedByBoth, 1)));
    }

    [Fact]
    public void Open_segment_row_is_open()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA, cars: 1));
        Assert.Equal(TripBookingStates.Confirming, Compute(FourDayTrip(), plan, Segment(TripEnquiryStatuses.FareSent, 1)));
    }

    [Fact]
    public void Segment_and_local_taxi_on_the_travel_day_never_merge()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA, cars: 1));
        var local = Taxi(TripEnquiryStatuses.BookedByTraveller, "[3,4]", fare: 3000);
        local.CheckIn = Start.AddDays(2);
        local.CheckOut = Start.AddDays(4);
        var c = TripBookingSummary.Count(plan, [local]);
        Assert.Equal(2, c.TaxisTotal);
        Assert.Equal(0, c.TaxisConfirmed);
        Assert.Equal(0, c.TravelConfirmed);
        Assert.Equal(1, c.TravelTotal);
        Assert.Equal(TripBookingStates.Confirming, Compute(FourDayTrip(), plan, local));
    }

    [Fact]
    public void AllSettled_needs_every_journey_car_confirmed()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA));
        Assert.False(TripReminderRules.AllSettled(plan, []));
        Assert.False(TripReminderRules.AllSettled(plan, [Segment(TripEnquiryStatuses.ConfirmedByBoth, 1)]));
        Assert.False(TripReminderRules.AllSettled(plan,
            [Segment(TripEnquiryStatuses.BookedByTraveller, 1), Segment(TripEnquiryStatuses.BookedByTraveller, 2)]));
        Assert.True(TripReminderRules.AllSettled(plan,
            [Segment(TripEnquiryStatuses.ConfirmedByBoth, 1), Segment(TripEnquiryStatuses.ConfirmedByBoth, 2)]));
    }

    [Fact]
    public void NoReplySegmentKey_is_short_and_keyed_by_trip_and_date()
    {
        var trip = Guid.NewGuid();
        var key = TripReminderRules.NoReplySegmentKey(trip, new DateOnly(2026, 11, 3));
        Assert.StartsWith("noreply:seg:", key);
        Assert.EndsWith(":20261103", key);
        Assert.Contains(trip.ToString("N"), key);
        Assert.True(key.Length <= 80);
        Assert.NotEqual(TripReminderRules.NoReplyKey(trip), key);
    }

    // ---- No stay tonight on a two-city trip (CONTRACT_MCC D0): night 2 = Haridwar's last night, spent on the road

    private static TripPlan TwoCityWithRoadNight()
    {
        var plan = TwoCityPlan(null);
        plan.Days[1].Stay = new TripStayChoice { Name = "No stay tonight", Skipped = true };
        return plan;
    }

    [Fact]
    public void Skipped_leg_end_night_with_the_other_nights_booked_is_ready_and_all_settled()
    {
        var plan = TwoCityWithRoadNight();
        TripEnquiry[] rows = [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1), Stay(TripEnquiryStatuses.ConfirmedByBoth, 3, 4)];

        Assert.Equal(TripBookingStates.Ready, ComputeFull(FourDayTrip(), plan, rows));
        Assert.True(TripReminderRules.AllSettled(plan, rows));
    }

    [Fact]
    public void Skipped_night_with_an_open_stay_row_on_it_is_confirming()
    {
        var plan = TwoCityWithRoadNight();
        TripEnquiry[] rows =
        [
            Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1), Stay(TripEnquiryStatuses.ConfirmedByBoth, 3, 4),
            Stay(TripEnquiryStatuses.Asked, 2, 2),
        ];

        Assert.Equal(TripBookingStates.Confirming, Compute(FourDayTrip(), plan, rows));
        Assert.False(TripReminderRules.AllSettled(plan, rows));
    }

    [Fact]
    public void Skipped_night_is_kept_out_of_the_stay_total()
    {
        var plan = TwoCityWithRoadNight();
        var c = TripBookingSummary.Count(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1), Stay(TripEnquiryStatuses.ConfirmedByBoth, 3, 4)]);

        Assert.Equal(3, c.StaysTotal);
        Assert.Equal(3, c.StaysConfirmed);
        Assert.Equal(0, c.StaysOwn);
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

    [Fact]
    public void StartsInSummary_two_cities_with_own_travel_says_travel_booked()
    {
        var plan = TwoCityPlan(new TripTravel { Mode = TripTravelModes.Own, FromName = "Haridwar", How = "bus" });
        Assert.Equal("No stay yet. No taxi yet. Travel booked.", TripReminderRules.StartsInSummary(plan, []));
    }

    [Fact]
    public void StartsInSummary_two_cities_with_an_unbooked_journey()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA));
        Assert.Equal("No stay yet. No taxi yet. 1 travel not booked.", TripReminderRules.StartsInSummary(plan, []));
        Assert.Equal("No stay yet. No taxi yet. Travel booked.", TripReminderRules.StartsInSummary(plan,
            [Segment(TripEnquiryStatuses.BookedByTraveller, 1), Segment(TripEnquiryStatuses.ConfirmedByBoth, 2)]));
    }

    [Fact]
    public void StartsInSummary_order_is_stay_taxi_travel_getting_there()
    {
        var plan = TwoCityPlan(JourneyArrival(JourneyA));
        plan.Steps = new TripSteps { StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 } };
        Assert.Equal("No stay yet. No taxi yet. 1 travel not booked. Getting there not sorted.",
            TripReminderRules.StartsInSummary(plan, []));
    }

    [Fact]
    public void StartsInSummary_two_cities_without_travel_adds_nothing()
        => Assert.Equal("No stay yet. No taxi yet.", TripReminderRules.StartsInSummary(TwoCityPlan(null), []));

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

    [Fact]
    public void Getting_there_journey_needs_every_car_confirmed()
    {
        SavedJourney[] journeys = [Saved(StepJourney, 2, Start)];
        var stay = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3);
        var car1 = StepRow(TripEnquiryStatuses.ConfirmedByBoth, 1, TripStepRoles.GettingThere, Start);
        TripEnquiry[] one = [stay, car1];
        Assert.Equal(TripBookingStates.Confirming, ComputeWith(PlannedTrip(), WithGettingThereJourney(), journeys, one));
        TripEnquiry[] asked = [stay, car1, StepRow(TripEnquiryStatuses.Asked, 2, TripStepRoles.GettingThere, Start)];
        Assert.False(TripReminderRules.AllSettled(WithGettingThereJourney(), asked));

        TripEnquiry[] both = [stay, car1, StepRow(TripEnquiryStatuses.ConfirmedByBoth, 2, TripStepRoles.GettingThere, Start)];
        Assert.Equal(TripBookingStates.Ready, ComputeWith(PlannedTrip(), WithGettingThereJourney(), journeys, both));
        Assert.True(TripReminderRules.AllSettled(WithGettingThereJourney(), both));
    }

    [Fact]
    public void Getting_there_own_car_settles_that_car()
    {
        SavedJourney[] journeys = [Saved(StepJourney, 2, Start)];
        TripEnquiry[] rows = [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3), StepRow(TripEnquiryStatuses.ConfirmedByBoth, 1, TripStepRoles.GettingThere, Start)];
        Assert.Equal(TripBookingStates.Ready, ComputeWith(PlannedTrip(), WithGettingThereJourney([2]), journeys, rows));
    }

    [Fact]
    public void Getting_there_row_on_another_date_is_red()
    {
        SavedJourney[] journeys = [Saved(StepJourney, 1, Start)];
        TripEnquiry[] rows = [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3), StepRow(TripEnquiryStatuses.ConfirmedByBoth, 1, TripStepRoles.GettingThere, Start.AddDays(-1))];
        Assert.False(TripReminderRules.AllSettled(WithGettingThereJourney(), rows));

        var dayBefore = WithGettingThereJourney();
        dayBefore.Steps!.GettingThere!.Date = Start.AddDays(-1);
        Assert.True(TripReminderRules.AllSettled(dayBefore, rows));
    }

    [Fact]
    public void Step_journey_cars_stay_out_of_the_travel_counts()
    {
        var c = TripBookingSummary.Count(WithGettingThereJourney(), [StepRow(TripEnquiryStatuses.ConfirmedByBoth, 1, TripStepRoles.GettingThere, Start)]);
        Assert.Equal(0, c.TravelTotal);
        Assert.Equal(0, c.TravelConfirmed);
        Assert.Equal(0, c.TaxisTotal);
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
}
