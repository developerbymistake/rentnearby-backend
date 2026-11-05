namespace RentNearBy.Tests.Trips;

public class TripStepsRulesTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly TripStartFrom Delhi = new() { Name = "Delhi", Lat = 28.6139, Lon = 77.2090 };

    private static TripPlan Plan(int days = 3, DateOnly? start = null, TripSteps? steps = null)
    {
        var plan = new TripPlan { Settings = new TripSettings { StartDate = start }, Steps = steps };
        for (var d = 1; d <= days; d++) plan.Days.Add(new TripDay { Day = d });
        return plan;
    }

    private static SavedJourney Journey(Guid? id = null, bool asked = false, bool deleted = false) => new()
    {
        Id = id ?? Guid.NewGuid(),
        FromName = "Delhi",
        FromLatitude = 28.6139m,
        FromLongitude = 77.2090m,
        ToName = "Nainital",
        ToLatitude = 29.3919m,
        ToLongitude = 79.4542m,
        TravelDate = Start,
        People = 4,
        Cars = 1,
        IsDeleted = deleted,
        Legs = { new SavedJourneyLeg { LegIndex = 1, DriverTaxiId = Guid.NewGuid(), LastEnquiryId = asked ? Guid.NewGuid() : null } },
    };

    [Fact]
    public void Carried_steps_never_pull_a_journey_from_another_trip()
    {
        var tripA = Guid.NewGuid();
        var tripB = Guid.NewGuid();
        var movedToB = Journey();
        movedToB.TripId = tripB;
        var ownA = Journey();
        ownA.TripId = tripA;
        var free = Journey();
        var steps = new TripSteps
        {
            StartFrom = Delhi,
            GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = movedToB.Id },
            ComingBack = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = ownA.Id },
        };

        var linkable = RentNearBy.Api.Handlers.TripHandlers.LinkableIds([movedToB, ownA, free], tripA);
        var keep = TripStepRules.Sanitise(steps, linkable);

        Assert.Equal(new[] { ownA.Id }, keep);
        Assert.Null(steps.GettingThere!.JourneyId);
        Assert.Equal(TripStepStatuses.NotStarted, steps.GettingThere.Status);
        Assert.Contains(free.Id, linkable);
    }

    [Fact]
    public void Sent_steps_may_link_a_journey_of_another_trip()
    {
        var other = Journey();
        other.TripId = Guid.NewGuid();

        Assert.Contains(other.Id, RentNearBy.Api.Handlers.TripHandlers.LinkableIds([other], null));
    }

    // About 1 km of latitude.
    private const double KmLat = 1 / 111.195;

    // ---- constants

    [Fact]
    public void Wire_values_match_the_contract()
    {
        Assert.Equal(new[] { "notStarted", "chosen", "booked", "own", "skipped", "notNeeded" }, TripStepStatuses.All);
        Assert.Equal("asked", TripStepDisplays.Asked);
        Assert.Equal("gettingThere", TripStepRoles.GettingThere);
        Assert.Equal("comingBack", TripStepRoles.ComingBack);
        Assert.Equal(5000.0, TripStepRules.SameCityMetres);
        Assert.Equal(4, TripPlan.CurrentVersion);
    }

    [Fact]
    public void Journeys_reach_a_trip_a_year_out()
    {
        Assert.Equal(365, JourneyRules.MaxDaysAhead);
        Assert.Equal(365, new TaxiWaysOptions().MaxDaysAhead);
        var today = new DateOnly(2026, 11, 5);
        Assert.True(JourneyRules.IsTravelDate(today.AddDays(365), today));
        Assert.False(JourneyRules.IsTravelDate(today.AddDays(366), today));
    }

    // ---- expected dates (night rule: check-out = start + dayCount)

    [Fact]
    public void ExpectedDate_getting_there_defaults_to_the_start()
        => Assert.Equal(Start, TripStepRules.ExpectedDate(TripStepRoles.GettingThere, Plan(start: Start)));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    [InlineData(14, 15)]
    public void ExpectedDate_coming_back_defaults_to_check_out(int days, int checkOutDay)
        => Assert.Equal(Start.AddDays(checkOutDay - 1), TripStepRules.ExpectedDate(TripStepRoles.ComingBack, Plan(days, Start)));

    [Fact]
    public void ExpectedDate_one_day_trip_comes_back_on_day_two()
        => Assert.Equal(new DateOnly(2026, 11, 2), TripStepRules.ExpectedDate(TripStepRoles.ComingBack, Plan(1, Start)));

    [Fact]
    public void ExpectedDate_step_date_wins()
    {
        var steps = new TripSteps
        {
            GettingThere = new TripStep { Date = Start.AddDays(-1) },
            ComingBack = new TripStep { Date = Start.AddDays(5) },
        };
        var plan = Plan(3, Start, steps);
        Assert.Equal(Start.AddDays(-1), TripStepRules.ExpectedDate(TripStepRoles.GettingThere, plan));
        Assert.Equal(Start.AddDays(5), TripStepRules.ExpectedDate(TripStepRoles.ComingBack, plan));
    }

    [Fact]
    public void ExpectedDate_without_dates_is_null()
    {
        Assert.Null(TripStepRules.ExpectedDate(TripStepRoles.GettingThere, Plan()));
        Assert.Null(TripStepRules.ExpectedDate(TripStepRoles.ComingBack, Plan()));
    }

    // ---- date / start changed

    [Fact]
    public void DateChanged_only_when_the_dates_differ()
    {
        Assert.False(TripStepRules.DateChanged(Start, Start));
        Assert.True(TripStepRules.DateChanged(Start.AddDays(1), Start));
        Assert.True(TripStepRules.DateChanged(Start.AddDays(-30), Start));
        Assert.False(TripStepRules.DateChanged(null, Start));
    }

    [Fact]
    public void StartChanged_uses_five_km()
    {
        Assert.False(TripStepRules.StartChanged(Delhi, Delhi.Lat + 4.9 * KmLat, Delhi.Lon));
        Assert.True(TripStepRules.StartChanged(Delhi, Delhi.Lat + 5.1 * KmLat, Delhi.Lon));
        Assert.False(TripStepRules.StartChanged(null, 0, 0));
    }

    [Fact]
    public void StartChanged_looks_at_from_for_getting_there_and_to_for_coming_back()
    {
        var j = Journey();
        Assert.False(TripStepRules.StartChanged(TripStepRoles.GettingThere, Delhi, j));
        Assert.True(TripStepRules.StartChanged(TripStepRoles.ComingBack, Delhi, j));

        var back = Journey();
        back.FromLatitude = 29.3919m; back.FromLongitude = 79.4542m;
        back.ToLatitude = 28.6139m; back.ToLongitude = 77.2090m;
        Assert.False(TripStepRules.StartChanged(TripStepRoles.ComingBack, Delhi, back));
        Assert.True(TripStepRules.StartChanged(TripStepRoles.GettingThere, Delhi, back));
    }

    // ---- display

    [Fact]
    public void Display_matrix()
    {
        var j = Journey();
        var asked = Journey(asked: true);

        Assert.Equal("notStarted", TripStepRules.Display(null, null));
        Assert.Equal("notStarted", TripStepRules.Display(new TripStep(), null));
        Assert.Equal("notStarted", TripStepRules.Display(new TripStep { Status = "notStarted" }, j));
        Assert.Equal("chosen", TripStepRules.Display(new TripStep { Status = "chosen", JourneyId = j.Id }, j));
        Assert.Equal("asked", TripStepRules.Display(new TripStep { Status = "chosen", JourneyId = asked.Id }, asked));
        Assert.Equal("booked", TripStepRules.Display(new TripStep { Status = "booked", JourneyId = asked.Id }, asked));
        Assert.Equal("own", TripStepRules.Display(new TripStep { Status = "own", Note = "Own car" }, null));
        Assert.Equal("skipped", TripStepRules.Display(new TripStep { Status = "skipped" }, null));
        Assert.Equal("notNeeded", TripStepRules.Display(new TripStep { Status = "notNeeded" }, null));
    }

    [Fact]
    public void Display_chosen_or_booked_without_its_journey_reads_not_started()
    {
        var j = Journey();
        Assert.Equal("notStarted", TripStepRules.Display(new TripStep { Status = "chosen", JourneyId = j.Id }, null));
        Assert.Equal("notStarted", TripStepRules.Display(new TripStep { Status = "booked", JourneyId = Guid.NewGuid() }, j));
        var gone = Journey(deleted: true);
        Assert.Equal("notStarted", TripStepRules.Display(new TripStep { Status = "chosen", JourneyId = gone.Id }, gone));
    }

    [Fact]
    public void IsSorted_everything_but_not_started()
    {
        Assert.False(TripStepRules.IsSorted("notStarted"));
        foreach (var d in new[] { "asked", "chosen", "booked", "own", "skipped", "notNeeded" })
            Assert.True(TripStepRules.IsSorted(d));
    }

    // ---- nudge

    [Fact]
    public void NeedsNudge_only_not_started_with_a_start()
    {
        Assert.False(TripStepRules.NeedsNudge(null));
        Assert.False(TripStepRules.NeedsNudge(new TripSteps()));
        Assert.True(TripStepRules.NeedsNudge(new TripSteps { StartFrom = Delhi }));
        Assert.True(TripStepRules.NeedsNudge(new TripSteps { StartFrom = Delhi, GettingThere = new TripStep { Status = "notStarted" } }));
        foreach (var s in new[] { "chosen", "booked", "own", "skipped", "notNeeded" })
            Assert.False(TripStepRules.NeedsNudge(new TripSteps { StartFrom = Delhi, GettingThere = new TripStep { Status = s } }));
    }

    // ---- sanitise

    [Fact]
    public void Sanitise_keeps_owned_journeys()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var steps = new TripSteps
        {
            GettingThere = new TripStep { Status = "chosen", JourneyId = a },
            ComingBack = new TripStep { Status = "booked", JourneyId = b },
        };

        var keep = TripStepRules.Sanitise(steps, new HashSet<Guid> { a, b });

        Assert.Equal(new[] { a, b }, keep);
        Assert.Equal(a, steps.GettingThere.JourneyId);
        Assert.Equal("booked", steps.ComingBack.Status);
    }

    [Fact]
    public void Sanitise_drops_a_foreign_id_and_resets_the_status()
    {
        var foreign = Guid.NewGuid();
        var steps = new TripSteps { GettingThere = new TripStep { Status = "booked", JourneyId = foreign } };

        var keep = TripStepRules.Sanitise(steps, new HashSet<Guid>());

        Assert.Empty(keep);
        Assert.Null(steps.GettingThere.JourneyId);
        Assert.Equal("notStarted", steps.GettingThere.Status);
    }

    [Fact]
    public void Sanitise_duplicate_id_keeps_getting_there()
    {
        var id = Guid.NewGuid();
        var steps = new TripSteps
        {
            GettingThere = new TripStep { Status = "chosen", JourneyId = id },
            ComingBack = new TripStep { Status = "chosen", JourneyId = id },
        };

        var keep = TripStepRules.Sanitise(steps, new HashSet<Guid> { id });

        Assert.Equal(new[] { id }, keep);
        Assert.Equal(id, steps.GettingThere.JourneyId);
        Assert.Null(steps.ComingBack.JourneyId);
        Assert.Equal("notStarted", steps.ComingBack.Status);
    }

    [Fact]
    public void Sanitise_drops_a_journey_on_an_own_step_but_keeps_its_status()
    {
        var id = Guid.NewGuid();
        var steps = new TripSteps { ComingBack = new TripStep { Status = "own", JourneyId = id, Note = "Train" } };

        var keep = TripStepRules.Sanitise(steps, new HashSet<Guid> { id });

        Assert.Empty(keep);
        Assert.Null(steps.ComingBack.JourneyId);
        Assert.Equal("own", steps.ComingBack.Status);
        Assert.Equal("Train", steps.ComingBack.Note);
    }

    [Fact]
    public void Sanitise_chosen_without_a_journey_goes_back_to_not_started()
    {
        var steps = new TripSteps { GettingThere = new TripStep { Status = "chosen" } };
        TripStepRules.Sanitise(steps, new HashSet<Guid>());
        Assert.Equal("notStarted", steps.GettingThere.Status);
    }

    [Fact]
    public void Sanitise_null_steps_links_nothing()
        => Assert.Empty(TripStepRules.Sanitise(null, new HashSet<Guid> { Guid.NewGuid() }));

    [Fact]
    public void ReferencedIds_are_distinct()
    {
        var id = Guid.NewGuid();
        var steps = new TripSteps { GettingThere = new TripStep { JourneyId = id }, ComingBack = new TripStep { JourneyId = id } };
        Assert.Equal(new[] { id }, TripStepRules.ReferencedIds(steps));
        Assert.Empty(TripStepRules.ReferencedIds(null));
    }

    [Fact]
    public void ClearDates_clears_both_step_dates()
    {
        var steps = new TripSteps
        {
            StartFrom = Delhi,
            GettingThere = new TripStep { Status = "chosen", JourneyId = Guid.NewGuid(), Date = Start },
            ComingBack = new TripStep { Date = Start.AddDays(3) },
        };
        TripStepRules.ClearDates(steps);
        Assert.Null(steps.GettingThere.Date);
        Assert.Null(steps.ComingBack.Date);
        Assert.NotNull(steps.GettingThere.JourneyId);
        TripStepRules.ClearDates(null);
    }

    // ---- merge (D1)

    [Fact]
    public void Merge_null_request_keeps_the_stored_steps()
    {
        var stored = new TripSteps { StartFrom = Delhi };
        Assert.Same(stored, TripStepsMerge.Resolve(stored, null));
    }

    [Fact]
    public void Merge_empty_object_clears_the_stored_steps()
    {
        var stored = new TripSteps { StartFrom = Delhi, GettingThere = new TripStep { Status = "own" } };
        var requested = new TripSteps();
        var result = TripStepsMerge.Resolve(stored, requested);
        Assert.Same(requested, result);
        Assert.Null(result!.StartFrom);
        Assert.Null(result.GettingThere);
    }

    [Fact]
    public void Merge_nothing_stored_nothing_sent_is_null()
        => Assert.Null(TripStepsMerge.Resolve(null, null));
}
