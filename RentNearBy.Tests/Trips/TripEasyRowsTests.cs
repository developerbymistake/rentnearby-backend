namespace RentNearBy.Tests.Trips;

// The Make it easy rows of CONTRACT_V2 1.5 and 2.3 (the app's trip_easy_rules_test.dart holds the same table).
public class TripEasyRowsTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly Guid GettingThereId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid SegmentId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");

    private static DateOnly DateOf(int day) => Start.AddDays(day - 1);

    private static TripDay Day(int d, int leg, int spots = 1)
    {
        var day = new TripDay { Day = d, Leg = leg };
        for (var i = 0; i < spots; i++) day.Spots.Add(new TripSpot { Id = $"s{d}{i}", Name = "Spot", VisitMinutes = 60 });
        return day;
    }

    private static TripPlan OneCity(int days = 2, bool dates = true)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = dates ? Start : null },
            Legs = [new TripCityLeg { Index = 1, CityKey = 1, CityName = "Nainital" }],
        };
        for (var d = 1; d <= days; d++) plan.Days.Add(Day(d, 1));
        return plan;
    }

    // Nainital days 1-2, Almora day 3.
    private static TripPlan TwoCity()
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs =
            [
                new TripCityLeg { Index = 1, CityKey = 1, CityName = "Nainital" },
                new TripCityLeg { Index = 2, CityKey = 2, CityName = "Almora" },
            ],
        };
        for (var d = 1; d <= 3; d++) plan.Days.Add(Day(d, d <= 2 ? 1 : 2));
        return plan;
    }

    private static Trip TripOf(TripPlan plan, string status = TripStatuses.Planned) => new()
    {
        Id = Guid.NewGuid(), Status = status, StartDate = plan.Settings.StartDate, EndDate = plan.EndDate,
    };

    private static TripEnquiry StayRow(string status, int night = 1, int nights = 1, string city = "Nainital") => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        Status = status,
        ListingName = "Deodar Homestay",
        Day = (short)night,
        CheckIn = DateOf(night),
        CheckOut = DateOf(night + nights),
        AdvanceStatus = TripAdvanceStatuses.None,
        ContextJson = "{\"city\":\"" + city + "\"}",
        UpdatedAt = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc),
    };

    private static TripEnquiry TaxiRow(string status, params int[] days) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = status,
        ListingName = "Pradeep Bisht",
        DaysJson = "[" + string.Join(",", days) + "]",
        CheckIn = DateOf(days[0]),
        CheckOut = DateOf(days[^1] + 1),
        AdvanceStatus = TripAdvanceStatuses.None,
        UpdatedAt = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc),
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
        ContextJson = "{\"city\":\"Nainital\",\"journeyId\":\"" + journeyId + "\",\"legIndex\":" + car + ",\"role\":\"" + role + "\"}",
    };

    private static SavedJourney Journey(Guid id, int cars, DateOnly date, string lastKnown = "Unknown", bool asked = false)
    {
        var journey = new SavedJourney { Id = id, TravelDate = date, Cars = (short)cars };
        for (var car = 1; car <= cars; car++)
            journey.Legs.Add(new SavedJourneyLeg
            {
                LegIndex = (short)car, DriverAvailable = true, LastKnownStatus = lastKnown, LastEnquiryId = asked ? Guid.NewGuid() : null,
            });
        return journey;
    }

    private static void ChooseGettingThere(TripPlan plan)
        => plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = GettingThereId } };

    private static List<TripEasyRow> Rows(TripPlan plan, IReadOnlyList<TripEnquiry>? rows = null, SavedJourney[]? journeys = null,
        IReadOnlySet<Guid>? revised = null)
        => TripEasyRows.Build(TripOf(plan), plan, rows ?? [], journeys ?? [], revised);

    private static TripEasyRow Row(IEnumerable<TripEasyRow> rows, string key) => rows.Single(r => r.Key.Id == key);

    private static string StateOf(TripPlan plan, string key, IReadOnlyList<TripEnquiry>? rows = null, SavedJourney[]? journeys = null)
        => Row(Rows(plan, rows, journeys), key).State;

    private static TripRowMark Own => new() { Status = TripRowMarkStatuses.Own };
    private static TripRowMark Skipped => new() { Status = TripRowMarkStatuses.Skipped };

    // ---- list

    [Fact]
    public void Row_order_and_keys_for_two_cities()
        => Assert.Equal(new[] { "gettingThere", "stay:1", "between:2", "stay:2", "taxi", "comingBack" },
            Rows(TwoCity()).Select(r => r.Key.Id).ToArray());

    [Fact]
    public void One_city_has_no_between_row()
        => Assert.Equal(new[] { "gettingThere", "stay:1", "taxi", "comingBack" }, Rows(OneCity()).Select(r => r.Key.Id).ToArray());

    [Fact]
    public void Key_ids_parse_back()
    {
        foreach (var id in new[] { "gettingThere", "stay:2", "between:3", "taxi", "comingBack" })
            Assert.Equal(id, TripRowKey.Parse(id)!.Id);
        Assert.Equal(new TripRowKey(TripRowKinds.Stay, 2), TripRowKey.Parse("stay:2"));
        Assert.Null(TripRowKey.Parse("stay"));
        Assert.Null(TripRowKey.Parse("stay:0"));
        Assert.Null(TripRowKey.Parse("taxi:1"));
        Assert.Null(TripRowKey.Parse("nope"));
        Assert.Null(TripRowKey.Parse(null));
    }

    [Fact]
    public void A_plan_without_dates_still_builds_every_row_as_todo()
        => Assert.All(Rows(OneCity(dates: false)), r => Assert.Equal(TripRowStates.Todo, r.State));

    [Fact]
    public void Wire_values()
    {
        Assert.Equal(new[] { "todo", "asked", "offer", "confirmed", "own", "saved", "skipped", "attention" },
            new[]
            {
                TripRowStates.Todo, TripRowStates.Asked, TripRowStates.Offer, TripRowStates.Confirmed, TripRowStates.Own,
                TripRowStates.Saved, TripRowStates.Skipped, TripRowStates.Attention,
            });
        Assert.Equal(new[] { "gettingThere", "stay", "between", "taxi", "comingBack" },
            new[] { TripRowKinds.GettingThere, TripRowKinds.Stay, TripRowKinds.Between, TripRowKinds.Taxi, TripRowKinds.ComingBack });
    }

    // ---- stay

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, TripRowStates.Asked, false)]
    [InlineData(TripEnquiryStatuses.Offered, TripRowStates.Offer, true)]
    [InlineData(TripEnquiryStatuses.Countered, TripRowStates.Asked, false)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripRowStates.Confirmed, false)]
    [InlineData(TripEnquiryStatuses.NotAvailable, TripRowStates.Attention, false)]
    [InlineData(TripEnquiryStatuses.Expired, TripRowStates.Attention, false)]
    [InlineData(TripEnquiryStatuses.Failed, TripRowStates.Attention, false)]
    [InlineData(TripEnquiryStatuses.CancelledByOwner, TripRowStates.Attention, false)]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller, TripRowStates.Todo, false)]
    [InlineData(TripEnquiryStatuses.Declined, TripRowStates.Todo, false)]
    [InlineData(TripEnquiryStatuses.Closed, TripRowStates.Todo, false)]
    public void Stay_state_from_the_enquiry(string status, string state, bool needsYou)
    {
        var row = Row(Rows(OneCity(), [StayRow(status, 1, 2)]), "stay:1");
        Assert.Equal(state, row.State);
        Assert.Equal(needsYou, row.NeedsYou);
    }

    [Fact]
    public void Stay_marks_win_and_skipped_leaves_the_count()
    {
        var plan = OneCity();
        plan.Legs![0].StayMark = Own;
        Assert.Equal(TripRowStates.Own, StateOf(plan, "stay:1"));
        plan.Legs[0].StayMark = Skipped;
        Assert.Equal(TripRowStates.Skipped, StateOf(plan, "stay:1"));
    }

    [Fact]
    public void Stay_confirmed_needs_you_for_a_pending_advance_a_revised_price_or_a_replacing_offer()
    {
        var plan = OneCity();
        var both = StayRow(TripEnquiryStatuses.ConfirmedByBoth, 1, 2);
        Assert.False(Row(Rows(plan, [both]), "stay:1").NeedsYou);

        both.AdvanceStatus = TripAdvanceStatuses.Pending;
        Assert.True(Row(Rows(plan, [both]), "stay:1").NeedsYou);

        both.AdvanceStatus = TripAdvanceStatuses.None;
        Assert.True(Row(Rows(plan, [both], [], new HashSet<Guid> { both.Id }), "stay:1").NeedsYou);
        Assert.True(Row(Rows(plan, [both, StayRow(TripEnquiryStatuses.Offered, 1, 2)]), "stay:1").NeedsYou);
    }

    [Fact]
    public void Stay_asked_count_is_the_live_rows_and_a_live_ask_beats_an_older_problem()
    {
        var plan = OneCity();
        var failed = StayRow(TripEnquiryStatuses.Failed, 1, 2);
        failed.UpdatedAt = failed.UpdatedAt.AddHours(-1);
        var rows = Rows(plan, [failed, StayRow(TripEnquiryStatuses.Asked, 1, 2), StayRow(TripEnquiryStatuses.Asked, 1, 2)]);
        Assert.Equal(TripRowStates.Asked, Row(rows, "stay:1").State);
        Assert.Equal(2, Row(rows, "stay:1").AskedCount);
    }

    [Fact]
    public void Stay_problem_the_traveller_dismissed_reads_todo_and_only_the_newest_closed_row_counts()
    {
        var plan = OneCity();
        var failed = StayRow(TripEnquiryStatuses.Failed, 1, 2);
        plan.DismissedEnquiryIds = [failed.Id];
        Assert.Equal(TripRowStates.Todo, StateOf(plan, "stay:1", [failed]));

        var older = StayRow(TripEnquiryStatuses.Failed, 1, 2);
        older.UpdatedAt = older.UpdatedAt.AddHours(-2);
        var newer = StayRow(TripEnquiryStatuses.CancelledByTraveller, 1, 2);
        Assert.Equal(TripRowStates.Todo, StateOf(OneCity(), "stay:1", [older, newer]));
    }

    [Fact]
    public void Each_city_row_reads_only_the_nights_of_its_city()
    {
        var plan = TwoCity();
        var rows = Rows(plan, [StayRow(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)]);
        Assert.Equal(TripRowStates.Confirmed, Row(rows, "stay:1").State);
        Assert.Equal(TripRowStates.Todo, Row(rows, "stay:2").State);

        var second = Rows(plan, [StayRow(TripEnquiryStatuses.Asked, 3, 1, "Almora")]);
        Assert.Equal(TripRowStates.Todo, Row(second, "stay:1").State);
        Assert.Equal(TripRowStates.Asked, Row(second, "stay:2").State);
    }

    [Fact]
    public void A_booking_of_another_city_moved_onto_the_nights_is_attention_not_done()
    {
        var plan = TwoCity();
        var moved = StayRow(TripEnquiryStatuses.ConfirmedByBoth, 3, 1, "Nainital");
        var row = Row(Rows(plan, [moved]), "stay:2");
        Assert.Equal(TripRowStates.Attention, row.State);
        Assert.True(row.NeedsYou);
    }

    [Fact]
    public void Booked_taxi_needs_you_only_when_another_fare_is_waiting()
    {
        var plan = OneCity();
        var booked = TaxiRow(TripEnquiryStatuses.BookedByTraveller, 1, 2);
        var fare = TaxiRow(TripEnquiryStatuses.FareSent, 1, 2);
        var row = Row(Rows(plan, [booked, fare]), "taxi");
        Assert.Equal(TripRowStates.Booked, row.State);
        Assert.True(row.NeedsYou);
        Assert.False(Row(Rows(plan, [booked]), "taxi").NeedsYou);
    }

    [Fact]
    public void Booked_row_counts_as_done_and_the_trip_is_ready()
    {
        var plan = OneCity();
        plan.Legs![0].StayMark = Skipped;
        plan.TaxiMark = null;
        var rows = Rows(plan, [TaxiRow(TripEnquiryStatuses.BookedByTraveller, 1, 2)]);
        var progress = TripEasyRows.Progress(rows);
        Assert.Equal(TripRowStates.Booked, Row(rows, "taxi").State);
        Assert.True(progress.Done >= 1);
        Assert.True(TripRowStates.IsDone(TripRowStates.Booked));
        Assert.False(TripRowStates.IsInProgress(TripRowStates.Booked));
        TripEasyRow R(string state) => new() { Key = new TripRowKey(TripRowKinds.Taxi, null), State = state };
        Assert.Equal(TripBookingStates.Ready, TripBookingStates.OfRows([R("booked"), R("skipped")]));
    }

    // ---- partial cover

    [Fact]
    public void Two_of_three_nights_asked_is_partial_and_not_done()
    {
        var plan = OneCity(3);
        var row = Row(Rows(plan, [StayRow(TripEnquiryStatuses.Asked, 1, 2)]), "stay:1");
        Assert.Equal(TripRowStates.Asked, row.State);
        Assert.Equal(2, row.NightsCovered);
        Assert.Equal(3, row.NightsTotal);
        Assert.True(row.Partial);
        var progress = TripEasyRows.Progress([row]);
        Assert.Equal(0, progress.Done);
        Assert.Equal(1, progress.InProgress);
        Assert.Equal(2, progress.Rows[0].NightsCovered);
        Assert.Equal(3, progress.Rows[0].NightsTotal);
    }

    [Fact]
    public void Two_of_three_nights_confirmed_is_confirmed_but_not_done_and_confirming()
    {
        var plan = OneCity(3);
        var rows = Rows(plan, [StayRow(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)]);
        var row = Row(rows, "stay:1");
        Assert.Equal(TripRowStates.Confirmed, row.State);
        Assert.True(row.Partial);
        Assert.False(TripEasyRows.IsRowDone(row));
        Assert.Equal(0, TripEasyRows.Progress([row]).Done);
        Assert.Equal(TripBookingStates.Confirming, TripBookingStates.OfRows([row]));
    }

    [Fact]
    public void Night_by_night_asks_cover_the_leg()
    {
        var plan = OneCity(2);
        var row = Row(Rows(plan, [StayRow(TripEnquiryStatuses.Asked, 1, 1), StayRow(TripEnquiryStatuses.Asked, 2, 1)]), "stay:1");
        Assert.Equal(2, row.NightsCovered);
        Assert.Equal(2, row.NightsTotal);
        Assert.False(row.Partial);
    }

    [Fact]
    public void A_one_night_trip_stay_row_is_unchanged_and_other_rows_carry_no_nights()
    {
        var plan = OneCity(1);
        var rows = Rows(plan, [StayRow(TripEnquiryStatuses.Asked, 1, 1)]);
        Assert.Equal(1, Row(rows, "stay:1").NightsTotal);
        Assert.False(Row(rows, "stay:1").Partial);
        Assert.Equal(0, Row(rows, "taxi").NightsTotal);
        Assert.Null(TripEasyRows.ToDto(Row(rows, "taxi")).NightsTotal);
    }

    // ---- taxi

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, TripRowStates.Asked, false)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, TripRowStates.Booked, false)]
    [InlineData(TripEnquiryStatuses.FareSent, TripRowStates.Offer, true)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripRowStates.Confirmed, false)]
    [InlineData(TripEnquiryStatuses.NotAvailable, TripRowStates.Attention, false)]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller, TripRowStates.Todo, false)]
    public void Taxi_state_from_the_enquiry(string status, string state, bool needsYou)
    {
        var row = Row(Rows(OneCity(), [TaxiRow(status, 1, 2)]), "taxi");
        Assert.Equal(state, row.State);
        Assert.Equal(needsYou, row.NeedsYou);
    }

    [Fact]
    public void Taxi_marks_and_a_confirmed_row_with_another_fare_waiting()
    {
        var plan = OneCity();
        plan.TaxiMark = Own;
        Assert.Equal(TripRowStates.Own, StateOf(plan, "taxi"));
        plan.TaxiMark = Skipped;
        Assert.Equal(TripRowStates.Skipped, StateOf(plan, "taxi"));

        var row = Row(Rows(OneCity(), [TaxiRow(TripEnquiryStatuses.ConfirmedByBoth, 1), TaxiRow(TripEnquiryStatuses.FareSent, 2)]), "taxi");
        Assert.Equal(TripRowStates.Confirmed, row.State);
        Assert.True(row.NeedsYou);
    }

    [Fact]
    public void Journey_rows_never_count_as_the_local_taxi()
    {
        var plan = OneCity();
        ChooseGettingThere(plan);
        var car = JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, GettingThereId, 1, Start, TripStepRoles.GettingThere);
        Assert.Equal(TripRowStates.Todo, StateOf(plan, "taxi", [car]));
    }

    // ---- getting there / coming back

    [Fact]
    public void Step_marks_own_and_skipped()
    {
        var plan = OneCity();
        plan.Steps = new TripSteps
        {
            GettingThere = new TripStep { Status = TripStepStatuses.Own },
            ComingBack = new TripStep { Status = TripStepStatuses.Skipped },
        };
        Assert.Equal(TripRowStates.Own, StateOf(plan, "gettingThere"));
        Assert.Equal(TripRowStates.Skipped, StateOf(plan, "comingBack"));
    }

    [Fact]
    public void Getting_there_journey_states()
    {
        var plan = OneCity();
        ChooseGettingThere(plan);

        Assert.Equal(TripRowStates.Saved, StateOf(plan, "gettingThere", [], [Journey(GettingThereId, 1, Start)]));
        Assert.Equal(TripRowStates.Asked, StateOf(plan, "gettingThere", [], [Journey(GettingThereId, 1, Start, asked: true)]));

        var free = Row(Rows(plan, [], [Journey(GettingThereId, 1, Start, JourneyLegStatuses.Free, asked: true)]), "gettingThere");
        Assert.Equal(TripRowStates.Offer, free.State);
        Assert.True(free.NeedsYou);

        var confirmed = JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, GettingThereId, 1, Start, TripStepRoles.GettingThere);
        Assert.Equal(TripRowStates.Confirmed, StateOf(plan, "gettingThere", [confirmed], [Journey(GettingThereId, 1, Start, asked: true)]));

        var booked = JourneyRow(TripEnquiryStatuses.BookedByTraveller, GettingThereId, 1, Start, TripStepRoles.GettingThere);
        Assert.Equal(TripRowStates.Booked, StateOf(plan, "gettingThere", [booked], [Journey(GettingThereId, 1, Start, asked: true)]));
    }

    [Fact]
    public void Getting_there_attention_when_the_date_moved_or_the_driver_is_gone()
    {
        var plan = OneCity();
        ChooseGettingThere(plan);
        Assert.Equal(TripRowStates.Attention, StateOf(plan, "gettingThere", [], [Journey(GettingThereId, 1, Start.AddDays(-1))]));

        var gone = Journey(GettingThereId, 1, Start, JourneyLegStatuses.NotFree, asked: true);
        Assert.Equal(TripRowStates.Attention, StateOf(plan, "gettingThere", [], [gone]));
    }

    [Fact]
    public void Getting_there_with_a_missing_journey_is_todo_and_every_car_own_is_own()
    {
        var plan = OneCity();
        ChooseGettingThere(plan);
        Assert.Equal(TripRowStates.Todo, StateOf(plan, "gettingThere"));

        plan.Steps!.GettingThere!.OwnCars = [1];
        Assert.Equal(TripRowStates.Own, StateOf(plan, "gettingThere", [], [Journey(GettingThereId, 1, Start)]));
    }

    [Fact]
    public void Coming_back_reads_its_own_leave_on_date()
    {
        var plan = OneCity();
        plan.Steps = new TripSteps { ComingBack = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = GettingThereId } };
        var onTime = Journey(GettingThereId, 1, plan.ComingBackDate!.Value);
        Assert.Equal(TripRowStates.Saved, StateOf(plan, "comingBack", [], [onTime]));
        Assert.Equal(TripRowStates.Attention, StateOf(plan, "comingBack", [], [Journey(GettingThereId, 1, Start.AddDays(-3))]));
    }

    // ---- between cities

    private static TripPlan WithArrival()
    {
        var plan = TwoCity();
        plan.Legs![1].Arrival = new TripTravel { FromName = "Nainital", JourneyId = SegmentId, Cars = 1 };
        return plan;
    }

    [Fact]
    public void Between_marks_and_no_travel_yet()
    {
        var plan = TwoCity();
        Assert.Equal(TripRowStates.Todo, StateOf(plan, "between:2"));
        plan.Legs![1].TravelMark = Own;
        Assert.Equal(TripRowStates.Own, StateOf(plan, "between:2"));
        plan.Legs[1].TravelMark = Skipped;
        Assert.Equal(TripRowStates.Skipped, StateOf(plan, "between:2"));
    }

    [Fact]
    public void Between_with_an_arrival_but_no_loaded_journey_is_asked()
        => Assert.Equal(TripRowStates.Asked, StateOf(WithArrival(), "between:2"));

    [Fact]
    public void Between_journey_states()
    {
        var plan = WithArrival();
        var date = DateOf(3);
        Assert.Equal(TripRowStates.Saved, StateOf(plan, "between:2", [], [Journey(SegmentId, 1, date)]));
        Assert.Equal(TripRowStates.Asked, StateOf(plan, "between:2", [], [Journey(SegmentId, 1, date, asked: true)]));
        Assert.Equal(TripRowStates.Attention, StateOf(plan, "between:2", [], [Journey(SegmentId, 1, date.AddDays(1))]));

        var confirmed = JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, SegmentId, 1, date, TripStepRoles.Segment);
        Assert.Equal(TripRowStates.Confirmed, StateOf(plan, "between:2", [confirmed], [Journey(SegmentId, 1, date, asked: true)]));
    }

    // ---- progress numbers

    [Fact]
    public void Fresh_plan_is_all_todo_zero_percent_and_next_is_the_first_row()
    {
        var progress = TripEasyRows.Progress(Rows(OneCity()));
        Assert.Equal(4, progress.Total);
        Assert.Equal(0, progress.Done);
        Assert.Equal(0, progress.InProgress);
        Assert.Equal(0, progress.Attention);
        Assert.Equal(0, progress.Percent);
        Assert.False(progress.NeedsYou);
        Assert.Equal("gettingThere", progress.Next!.Key);
        Assert.Equal(TripRowKinds.GettingThere, progress.Next.Kind);
        Assert.Null(progress.Next.LegIndex);
        Assert.Equal(4, progress.Rows.Count);
    }

    [Fact]
    public void Progress_counts_done_in_progress_and_leaves_skipped_out_of_the_total()
    {
        var plan = TwoCity();
        ChooseGettingThere(plan);
        plan.Steps!.ComingBack = new TripStep { Status = TripStepStatuses.Skipped };
        plan.Legs![1].TravelMark = Own;
        var rows = Rows(plan, [StayRow(TripEnquiryStatuses.Asked, 1, 2)], [Journey(GettingThereId, 1, Start)]);
        var progress = TripEasyRows.Progress(rows, new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new[] { "saved", "asked", "own", "todo", "todo", "skipped" }, rows.Select(r => r.State).ToArray());
        Assert.Equal(5, progress.Total);
        Assert.Equal(2, progress.Done);
        Assert.Equal(1, progress.InProgress);
        Assert.Equal(40, progress.Percent);
        Assert.Equal("stay:2", progress.Next!.Key);
        Assert.Equal(2, progress.Next.LegIndex);
        Assert.Equal(new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc), progress.LastEventAt);
        Assert.Equal("asked", progress.Rows.Single(r => r.Key == "stay:1").State);
        Assert.Equal(1, progress.Rows.Single(r => r.Key == "stay:1").AskedCount);
    }

    [Fact]
    public void Next_prefers_attention_or_a_row_that_needs_you_then_todo_then_in_progress()
    {
        var plan = OneCity();
        var progress = TripEasyRows.Progress(Rows(plan, [StayRow(TripEnquiryStatuses.Offered, 1, 2)]));
        Assert.True(progress.NeedsYou);
        Assert.Equal("stay:1", progress.Next!.Key);

        plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Own }, ComingBack = new TripStep { Status = TripStepStatuses.Own } };
        plan.TaxiMark = Own;
        var waiting = TripEasyRows.Progress(Rows(plan, [StayRow(TripEnquiryStatuses.Asked, 1, 2)]));
        Assert.Equal("stay:1", waiting.Next!.Key);
        Assert.False(waiting.NeedsYou);
        Assert.Equal(75, waiting.Percent);
    }

    [Fact]
    public void Everything_skipped_is_a_hundred_percent_of_nothing()
    {
        var plan = OneCity();
        plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Skipped }, ComingBack = new TripStep { Status = TripStepStatuses.Skipped } };
        plan.TaxiMark = Skipped;
        plan.Legs![0].StayMark = Skipped;
        var progress = TripEasyRows.Progress(Rows(plan));
        Assert.Equal(0, progress.Total);
        Assert.Equal(100, progress.Percent);
        Assert.Null(progress.Next);
    }

    [Fact]
    public void Compute_uses_the_newest_enquiry_change_as_the_last_event()
    {
        var plan = OneCity();
        var a = StayRow(TripEnquiryStatuses.Asked, 1, 2);
        var b = TaxiRow(TripEnquiryStatuses.Asked, 1);
        b.UpdatedAt = a.UpdatedAt.AddHours(3);
        Assert.Equal(b.UpdatedAt, TripEasyRows.Compute(TripOf(plan), plan, [a, b], []).LastEventAt);
        Assert.Null(TripEasyRows.Compute(TripOf(plan), plan, [], []).LastEventAt);
    }

    // ---- booking state

    private static string Booking(TripPlan plan, IReadOnlyList<TripEnquiry>? rows = null, string status = TripStatuses.Planned, SavedJourney[]? journeys = null)
        => TripBookingStates.Compute(TripOf(plan, status), plan, rows ?? [], journeys);

    private static void AllOwn(TripPlan plan)
    {
        plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Own }, ComingBack = new TripStep { Status = TripStepStatuses.Own } };
        plan.TaxiMark = Own;
        foreach (var leg in plan.Legs!)
        {
            leg.StayMark = Own;
            if (leg.Index >= 2) leg.TravelMark = Own;
        }
    }

    [Fact]
    public void Booking_state_matrix()
    {
        Assert.Equal(TripBookingStates.Completed, Booking(OneCity(), [StayRow(TripEnquiryStatuses.Asked)], TripStatuses.Completed));
        Assert.Equal(TripBookingStates.Draft, Booking(OneCity(), null, TripStatuses.Draft));
        Assert.Equal(TripBookingStates.Draft, Booking(OneCity(0)));
        Assert.Equal(TripBookingStates.PlanOnly, Booking(OneCity()));
        Assert.Equal(TripBookingStates.PlanOnly, Booking(OneCity(dates: false)));
        Assert.Equal(TripBookingStates.PlanOnly, Booking(OneCity(), [StayRow(TripEnquiryStatuses.Closed)]));
        Assert.Equal(TripBookingStates.Confirming, Booking(OneCity(), [StayRow(TripEnquiryStatuses.Asked)]));
        Assert.Equal(TripBookingStates.Confirming, Booking(OneCity(), [StayRow(TripEnquiryStatuses.Failed, 1, 2)]));

        var plan = OneCity();
        AllOwn(plan);
        Assert.Equal(TripBookingStates.Ready, Booking(plan));
        plan.Legs![0].StayMark = null;
        Assert.Equal(TripBookingStates.Confirming, Booking(plan, [StayRow(TripEnquiryStatuses.Offered, 1, 2)]));
        Assert.Equal(TripBookingStates.Ready, Booking(plan, [StayRow(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)]));
    }

    [Fact]
    public void Booking_state_one_row_done_and_the_rest_todo_is_confirming_and_all_skipped_is_plan_only()
    {
        var plan = OneCity();
        plan.TaxiMark = Own;
        Assert.Equal(TripBookingStates.Confirming, Booking(plan));

        var skipped = OneCity();
        skipped.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Skipped }, ComingBack = new TripStep { Status = TripStepStatuses.Skipped } };
        skipped.TaxiMark = Skipped;
        skipped.Legs![0].StayMark = Skipped;
        Assert.Equal(TripBookingStates.PlanOnly, Booking(skipped));
    }

    [Fact]
    public void Booking_state_ready_with_a_confirmed_stay_and_confirmed_journey_cars()
    {
        var plan = TwoCity();
        AllOwn(plan);
        plan.Legs![0].StayMark = null;
        plan.Legs[1].TravelMark = null;
        plan.Legs[1].Arrival = new TripTravel { FromName = "Nainital", JourneyId = SegmentId, Cars = 1 };
        var date = DateOf(3);
        var journeys = new[] { Journey(SegmentId, 1, date, asked: true) };
        var stay = StayRow(TripEnquiryStatuses.ConfirmedByBoth, 1, 2);
        Assert.Equal(TripBookingStates.Confirming, Booking(plan, [stay], journeys: journeys));

        var car = JourneyRow(TripEnquiryStatuses.ConfirmedByBoth, SegmentId, 1, date, TripStepRoles.Segment);
        Assert.Equal(TripBookingStates.Ready, Booking(plan, [stay, car], journeys: journeys));
    }

    [Fact]
    public void Row_states_to_booking_state_helper()
    {
        TripEasyRow R(string state) => new() { Key = new TripRowKey(TripRowKinds.Taxi, null), State = state };
        Assert.Equal(TripBookingStates.PlanOnly, TripBookingStates.OfRows([R("todo"), R("skipped")]));
        Assert.Equal(TripBookingStates.Confirming, TripBookingStates.OfRows([R("todo"), R("own")]));
        Assert.Equal(TripBookingStates.Confirming, TripBookingStates.OfRows([R("attention"), R("own")]));
        Assert.Equal(TripBookingStates.Ready, TripBookingStates.OfRows([R("saved"), R("own"), R("confirmed"), R("skipped")]));
    }

    // ---- agreed total and titles

    [Fact]
    public void Agreed_total_sums_each_booked_row_once()
    {
        var stay = StayRow(TripEnquiryStatuses.ConfirmedByBoth, 1, 2);
        stay.AgreedTotal = 4800;
        var taxi = TaxiRow(TripEnquiryStatuses.BookedByTraveller, 1, 2);
        taxi.AgreedFare = 3400;
        var cancelled = StayRow(TripEnquiryStatuses.CancelledByOwner, 1, 2);
        cancelled.AgreedTotal = 999;
        Assert.Equal(8200, TripEasyRows.AgreedTotal([stay, stay, taxi, cancelled]));
        Assert.Equal(0, TripEasyRows.AgreedTotal([]));
    }

    [Fact]
    public void Titles()
    {
        var plan = TwoCity();
        string T(string key) => TripEasyRows.Title(TripRowKey.Parse(key)!, plan);
        Assert.Equal("Getting there", T("gettingThere"));
        Assert.Equal("Stay in Nainital", T("stay:1"));
        Assert.Equal("Stay in Almora", T("stay:2"));
        Assert.Equal("Nainital → Almora", T("between:2"));
        Assert.Equal("Local taxi", T("taxi"));
        Assert.Equal("Coming back", T("comingBack"));
        Assert.Equal("Local taxi", TripEasyRows.Title(Row(Rows(plan), "taxi"), plan));
    }
}
