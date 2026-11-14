namespace RentNearBy.Tests.Trips;

// Owner E, part 2: complete gate (M4), guide gate (B3), mark guard (M6), booked range (M7), plan status (M8), auto-complete (m22).
public class TripJourneyIntegrityE2Tests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static DateOnly DateOf(int day) => Start.AddDays(day - 1);

    private static TripPlan OneCity(int days = 3)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs = [new TripCityLeg { Index = 1, CityKey = 1, CityName = "Nainital" }],
        };
        for (var d = 1; d <= days; d++)
        {
            var day = new TripDay { Day = d, Leg = 1 };
            day.Spots.Add(new TripSpot { Id = $"s{d}", Name = "Spot", VisitMinutes = 60 });
            plan.Days.Add(day);
        }
        return plan;
    }

    private static Trip TripOf(TripPlan plan, string status = TripStatuses.Planned)
        => new() { Id = Guid.NewGuid(), Status = status, StartDate = plan.Settings.StartDate, EndDate = plan.EndDate };

    private static TripEnquiry Stay(string status, int night, int nights) => new()
    {
        Id = Guid.NewGuid(), TripId = Guid.NewGuid(), Kind = TripEnquiryKinds.Stay, Status = status, ListingName = "Homestay",
        Day = (short)night, CheckIn = DateOf(night), CheckOut = DateOf(night + nights), AdvanceStatus = TripAdvanceStatuses.None,
        ContextJson = "{\"city\":\"Nainital\"}", UpdatedAt = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc),
    };

    private static List<TripEasyRow> Rows(TripPlan plan, params TripEnquiry[] rows) => TripEasyRows.Build(TripOf(plan), plan, rows, []);

    private static readonly TripRowMark Own = new() { Status = TripRowMarkStatuses.Own };
    private static readonly TripRowMark Skipped = new() { Status = TripRowMarkStatuses.Skipped };

    private static void AllMarked(TripPlan plan, TripRowMark mark)
    {
        var s = mark.Status == TripRowMarkStatuses.Own ? TripStepStatuses.Own : TripStepStatuses.Skipped;
        plan.Steps = new TripSteps { GettingThere = new TripStep { Status = s }, ComingBack = new TripStep { Status = s } };
        plan.TaxiMark = mark;
        foreach (var leg in plan.Legs!) leg.StayMark = mark;
    }

    // ---- M4: complete

    [Fact]
    public void M4_day1_with_an_asked_row_is_not_ended()
    {
        var plan = OneCity();
        var rows = Rows(plan, Stay(TripEnquiryStatuses.Asked, 1, 3));
        Assert.Equal(TripCompleteVerdict.NotEnded, TripCompleteRules.Check(TripOf(plan), rows, DateOf(1)));
    }

    [Fact]
    public void M4_early_complete_works_when_every_row_is_settled_and_one_is_done()
    {
        var plan = OneCity();
        AllMarked(plan, Own);
        Assert.Equal(TripCompleteVerdict.Ok, TripCompleteRules.Check(TripOf(plan), Rows(plan), DateOf(1)));
    }

    [Fact]
    public void M4_all_skipped_cannot_complete_early()
    {
        var plan = OneCity();
        AllMarked(plan, Skipped);
        Assert.Equal(TripCompleteVerdict.NotEnded, TripCompleteRules.Check(TripOf(plan), Rows(plan), DateOf(1)));
    }

    [Fact]
    public void M4_on_the_last_day_it_works_even_with_open_rows()
    {
        var plan = OneCity();
        var rows = Rows(plan, Stay(TripEnquiryStatuses.Asked, 1, 3));
        Assert.Equal(TripCompleteVerdict.Ok, TripCompleteRules.Check(TripOf(plan), rows, DateOf(3)));
        Assert.Equal(TripCompleteVerdict.Ok, TripCompleteRules.Check(TripOf(plan), rows, DateOf(9)));
    }

    [Fact]
    public void M4_undated_trip_needs_a_settled_row()
    {
        var plan = OneCity();
        plan.Settings.StartDate = null;
        var trip = new Trip { Id = Guid.NewGuid(), Status = TripStatuses.Planned };
        Assert.Equal(TripCompleteVerdict.DatesRequired, TripCompleteRules.Check(trip, Rows(plan), DateOf(1)));
        AllMarked(plan, Own);
        Assert.Equal(TripCompleteVerdict.Ok, TripCompleteRules.Check(trip, Rows(plan), DateOf(1)));
    }

    [Fact]
    public void M4_draft_cannot_complete()
    {
        var plan = OneCity();
        AllMarked(plan, Own);
        Assert.Equal(TripCompleteVerdict.NotPlanned, TripCompleteRules.Check(TripOf(plan, TripStatuses.Draft), Rows(plan), DateOf(9)));
    }

    // ---- m22: an untouched trip is never auto-completed

    [Fact]
    public void M22_untouched_trip_is_untouched_and_a_trip_with_any_work_is_not()
    {
        var plan = OneCity();
        Assert.True(TripCompleteRules.IsUntouched(Rows(plan)));
        Assert.False(TripCompleteRules.IsUntouched(Rows(plan, Stay(TripEnquiryStatuses.Closed, 1, 1), Stay(TripEnquiryStatuses.Asked, 1, 3))));
        AllMarked(plan, Skipped);
        Assert.False(TripCompleteRules.IsUntouched(Rows(plan)));
    }

    // ---- B3: guide gate

    [Fact]
    public void B3_a_step_ahead_of_the_first_unresolved_row_is_stored_as_sent()
    {
        var plan = OneCity();
        Assert.Equal("stay:1", TripGuideGate.Apply(new TripGuide { Step = "stay:1" }, Rows(plan))!.Step);
        Assert.Equal("taxi", TripGuideGate.Apply(new TripGuide { Step = "taxi" }, Rows(plan))!.Step);
        Assert.Equal("gettingThere", TripGuideGate.Apply(new TripGuide { Step = "gettingThere" }, Rows(plan))!.Step);
    }

    [Fact]
    public void B3_a_step_the_plan_does_not_have_is_dropped_not_refused()
    {
        var plan = OneCity();
        Assert.Null(TripGuideGate.Apply(new TripGuide { Step = "stay:9" }, Rows(plan)));
        Assert.Null(TripGuideGate.Apply(new TripGuide { Step = "between:2" }, Rows(plan)));
        var kept = TripGuideGate.Apply(new TripGuide { Step = "stay:9", Overview = true }, Rows(plan))!;
        Assert.Null(kept.Step);
        Assert.True(kept.Overview);
    }

    [Fact]
    public void B3_multi_city_steps_are_all_stored_as_sent()
    {
        var plan = OneCity();
        plan.Legs!.Add(new TripCityLeg { Index = 2, CityKey = 2, CityName = "Almora" });
        plan.Days.Add(new TripDay { Day = 4, Leg = 2, Spots = { new TripSpot { Id = "s4", Name = "Spot", VisitMinutes = 60 } } });
        var rows = Rows(plan);
        foreach (var key in new[] { "stay:1", "taxi", "between:2", "stay:2" })
            Assert.Equal(key, TripGuideGate.Apply(new TripGuide { Step = key }, rows)!.Step);
    }

    [Fact]
    public void B3_not_now_keeps_plan_keys_deduped_and_capped()
    {
        var plan = OneCity();
        var guide = TripGuideGate.Apply(new TripGuide { NotNow = ["gettingThere", "gettingThere", "stay:9", "stay:1"] }, Rows(plan))!;
        Assert.Equal(new[] { "gettingThere", "stay:1" }, guide.NotNow);
        Assert.Null(TripGuideGate.Apply(new TripGuide { NotNow = ["stay:9"] }, Rows(plan)));
        var many = TripGuideRules.Sanitize(new TripGuide { NotNow = Enumerable.Range(1, 12).Select(i => $"stay:{i}").ToList() })!;
        Assert.Equal(TripGuideRules.MaxNotNow, many.NotNow!.Count);
    }

    [Fact]
    public void B3_validator_rule_rejects_a_malformed_or_oversized_not_now()
    {
        Assert.True(TripGuideRules.IsValid(new TripGuide { NotNow = ["gettingThere", "stay:1"] }));
        Assert.False(TripGuideRules.IsValid(new TripGuide { NotNow = ["stay:0"] }));
        Assert.False(TripGuideRules.IsValid(new TripGuide { NotNow = Enumerable.Range(1, 9).Select(i => $"stay:{i}").ToList() }));
    }

    [Fact]
    public void B3_find_without_evidence_is_dropped_and_with_an_open_row_is_kept()
    {
        var plan = OneCity();
        var guide = new TripGuide { Step = "stay:1", Choices = new() { ["stay:1"] = "find", ["taxi"] = "find" } };
        var bare = TripGuideGate.Apply(new TripGuide { Step = "gettingThere", Choices = guide.Choices }, Rows(plan));
        Assert.Null(bare!.Choices);

        plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Own } };
        var asked = TripGuideGate.Apply(guide, Rows(plan, Stay(TripEnquiryStatuses.Asked, 1, 3)));
        Assert.Equal("find", asked!.Choices!["stay:1"]);
        Assert.False(asked.Choices.ContainsKey("taxi"));
    }

    [Fact]
    public void B3_own_and_skip_choices_need_the_mark_and_unknown_keys_are_dropped()
    {
        var plan = OneCity();
        plan.TaxiMark = Own;
        plan.Legs![0].StayMark = Skipped;
        var guide = new TripGuide
        {
            Step = "gettingThere",
            Choices = new() { ["taxi"] = "own", ["stay:1"] = "skip", ["comingBack"] = "skip", ["stay:7"] = "own" },
        };
        var choices = TripGuideGate.Apply(guide, Rows(plan))!.Choices!;
        Assert.Equal("own", choices["taxi"]);
        Assert.Equal("skip", choices["stay:1"]);
        Assert.False(choices.ContainsKey("comingBack"));
        Assert.False(choices.ContainsKey("stay:7"));
    }

    [Fact]
    public void B3_a_todo_row_with_not_now_and_a_shortlist_is_not_done_in_progress()
    {
        var plan = OneCity();
        plan.Guide = new TripGuide { Step = "stay:1", NotNow = ["gettingThere"] };
        var rows = Rows(plan);
        var progress = TripEasyRows.Progress(rows, null, "stay:1");
        Assert.Equal(TripRowStates.Todo, rows.First(r => r.Key.Id == "gettingThere").State);
        Assert.Equal(0, progress.Done);
        Assert.Equal(0, progress.Percent);
    }

    [Fact]
    public void B3_no_guide_stays_no_guide()
        => Assert.Null(TripGuideGate.Apply(null, Rows(OneCity())));

    // ---- M6: a mark cannot hide a live enquiry

    [Fact]
    public void M6_own_over_an_asked_stay_is_blocked()
    {
        var stored = OneCity();
        var storedRows = Rows(stored, Stay(TripEnquiryStatuses.Asked, 1, 3));
        var incoming = OneCity();
        incoming.Legs![0].StayMark = Own;
        var incomingRows = Rows(incoming, Stay(TripEnquiryStatuses.Asked, 1, 3));
        Assert.Equal("stay:1", TripMarkGuard.BlockedKey(storedRows, incomingRows));
    }

    [Fact]
    public void M6_own_over_a_confirmed_stay_is_blocked()
    {
        var confirmed = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3);
        var storedRows = Rows(OneCity(), confirmed);
        var incoming = OneCity();
        incoming.Legs![0].StayMark = Skipped;
        Assert.Equal("stay:1", TripMarkGuard.BlockedKey(storedRows, Rows(incoming, confirmed)));
    }

    [Fact]
    public void M6_own_on_a_todo_row_and_an_unchanged_mark_are_allowed()
    {
        var plan = OneCity();
        var incoming = OneCity();
        incoming.Legs![0].StayMark = Own;
        incoming.TaxiMark = Skipped;
        Assert.Null(TripMarkGuard.BlockedKey(Rows(plan), Rows(incoming)));
        Assert.Null(TripMarkGuard.BlockedKey(Rows(incoming), Rows(incoming)));
    }

    [Fact]
    public void M6_a_closed_row_does_not_block_a_mark()
    {
        var closed = Stay(TripEnquiryStatuses.Declined, 1, 3);
        var incoming = OneCity();
        incoming.Legs![0].StayMark = Own;
        Assert.Null(TripMarkGuard.BlockedKey(Rows(OneCity(), closed), Rows(incoming, closed)));
    }

    // ---- M7: a cut cannot strand a booked night

    [Fact]
    public void M7_cutting_the_day_of_a_confirmed_night_is_refused()
    {
        var booked = Stay(TripEnquiryStatuses.ConfirmedByBoth, 3, 1);
        Assert.True(TripBookedRange.Strands(Start, 3, Start, 2, [booked]));
    }

    [Fact]
    public void M7_moving_the_start_past_a_confirmed_night_is_refused()
    {
        var booked = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1);
        Assert.True(TripBookedRange.Strands(Start, 3, Start.AddDays(1), 3, [booked]));
    }

    [Fact]
    public void M7_edits_that_keep_the_booked_nights_inside_are_allowed()
    {
        var booked = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 2);
        Assert.False(TripBookedRange.Strands(Start, 3, Start, 3, [booked]));
        Assert.False(TripBookedRange.Strands(Start, 3, Start, 2, [booked]));
        Assert.False(TripBookedRange.Strands(Start, 3, Start.AddDays(-1), 4, [booked]));
        Assert.False(TripBookedRange.Strands(Start, 3, Start, 2, [Stay(TripEnquiryStatuses.Asked, 3, 1)]));
    }

    [Fact]
    public void M7_a_booking_outside_the_trip_is_attention_not_todo()
    {
        var plan = OneCity(2);
        var rows = Rows(plan, Stay(TripEnquiryStatuses.ConfirmedByBoth, 4, 1));
        var stay = rows.Single(r => r.Key.Id == "stay:1");
        Assert.Equal(TripRowStates.Attention, stay.State);
        Assert.True(stay.NeedsYou);
    }

    // ---- M8: Planned stays Planned with a live enquiry

    [Fact]
    public void M8_planned_trip_with_live_enquiry_is_never_demoted_even_at_zero_spots()
    {
        Assert.Equal(TripStatuses.Planned, TripStatusRules.OnSave(TripStatuses.Planned, TripStatuses.Draft, 0, hasLiveEnquiry: true));
        Assert.Equal(TripStatuses.Planned, TripStatusRules.OnSave(TripStatuses.Planned, TripStatuses.Draft, 3, hasLiveEnquiry: true));
        Assert.Equal(TripStatuses.Draft, TripStatusRules.OnSave(TripStatuses.Planned, TripStatuses.Draft, 0, hasLiveEnquiry: false));
        Assert.True(TripStatusRules.RejectsZeroSpots(TripStatuses.Planned, 0, hasLiveEnquiry: true));
        Assert.False(TripStatusRules.RejectsZeroSpots(TripStatuses.Planned, 2, hasLiveEnquiry: true));
        Assert.False(TripStatusRules.RejectsZeroSpots(TripStatuses.Planned, 0, hasLiveEnquiry: false));
        Assert.False(TripStatusRules.RejectsZeroSpots(TripStatuses.Draft, 0, hasLiveEnquiry: true));
    }
}
