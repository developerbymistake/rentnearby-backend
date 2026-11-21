namespace RentNearBy.Tests.Trips;

// Owner E: lifecycle and progress loopholes (JOURNEY_INTEGRITY B4, M9, M12-M14). Negative and positive pairs.
public class TripJourneyIntegrityETests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly Guid JourneyId = Guid.Parse("eeeeeeee-0000-0000-0000-0000000000a1");

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
        Day = (short)night, CheckIn = DateOf(night), CheckOut = DateOf(night + nights),
        ContextJson = "{\"city\":\"Nainital\"}", UpdatedAt = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc),
    };

    private static TripEnquiry Taxi(string status, params int[] days) => new()
    {
        Id = Guid.NewGuid(), TripId = Guid.NewGuid(), Kind = TripEnquiryKinds.Taxi, Status = status, ListingName = "Driver",
        DaysJson = "[" + string.Join(",", days) + "]", CheckIn = DateOf(days[0]), CheckOut = DateOf(days[^1] + 1),
        UpdatedAt = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc),
    };

    private static SavedJourney Journey(bool asked = false) => new()
    {
        Id = JourneyId, TravelDate = Start, Cars = 1,
        Legs = { new SavedJourneyLeg { LegIndex = 1, DriverAvailable = true, LastKnownStatus = "Unknown", LastEnquiryId = asked ? Guid.NewGuid() : null } },
    };

    private static void ChooseGettingThere(TripPlan plan)
        => plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = JourneyId } };

    private static List<TripEasyRow> Rows(TripPlan plan, IReadOnlyList<TripEnquiry>? rows = null, SavedJourney[]? journeys = null)
        => TripEasyRows.BuildAll(TripOf(plan), plan, rows ?? [], journeys ?? []);

    private static TripEasyRow Row(IEnumerable<TripEasyRow> rows, string key) => rows.Single(r => r.Key.Id == key);

    private static readonly TripRowMark Own = new() { Status = TripRowMarkStatuses.Own };
    private static readonly TripRowMark Skipped = new() { Status = TripRowMarkStatuses.Skipped };

    private static void AllMarked(TripPlan plan, TripRowMark mark)
    {
        var s = mark.Status == TripRowMarkStatuses.Own ? TripStepStatuses.Own : TripStepStatuses.Skipped;
        plan.Steps = new TripSteps { GettingThere = new TripStep { Status = s }, ComingBack = new TripStep { Status = s } };
        plan.TaxiMark = mark;
        foreach (var leg in plan.Legs!) leg.StayMark = mark;
    }

    // ---- B4: a saved plan with no driver contacted is not done

    [Fact]
    public void B4_saved_state_is_not_done()
    {
        Assert.False(TripRowStates.IsDone(TripRowStates.Saved));
        Assert.True(TripRowStates.IsDone(TripRowStates.Own));
        Assert.True(TripRowStates.IsDone(TripRowStates.Confirmed));
        Assert.True(TripRowStates.IsDone(TripRowStates.Booked));
    }

    [Fact]
    public void B4_linked_journey_with_no_ask_is_planned_not_done_and_needs_the_traveller()
    {
        var plan = OneCity();
        ChooseGettingThere(plan);
        var row = Row(Rows(plan, null, [Journey()]), "gettingThere");
        Assert.Equal(TripRowStates.Saved, row.State);
        Assert.False(TripEasyRows.IsRowDone(row));
        Assert.True(row.NeedsYou);
    }

    [Fact]
    public void B4_progress_with_only_a_saved_journey_is_zero_and_booking_state_is_not_ready()
    {
        var plan = OneCity();
        AllMarked(plan, Own);
        plan.Steps!.GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = JourneyId };
        // Travel rows leave the progress: stay and taxi own + itinerary = 100.
        var progress = TripEasyRows.Progress(1, Rows(plan, null, [Journey()]));
        Assert.Equal(3, progress.Done);
        Assert.Equal(100, progress.Percent);

        var only = OneCity();
        ChooseGettingThere(only);
        Assert.Equal(1, TripEasyRows.Progress(1, Rows(only, null, [Journey()])).Done);
        Assert.Equal(TripBookingStates.PlanOnly, TripBookingStates.Compute(TripOf(only), only, [], [Journey()]));
    }

    [Fact]
    public void B4_asked_journey_still_reads_asked()
    {
        var plan = OneCity();
        ChooseGettingThere(plan);
        Assert.Equal(TripRowStates.Asked, Row(Rows(plan, null, [Journey(asked: true)]), "gettingThere").State);
    }

    // ---- M13: asked nights are not covered nights

    [Fact]
    public void M13_night1_confirmed_nights23_asked_is_partial_not_done()
    {
        var plan = OneCity();
        var rows = Rows(plan, [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 1), Stay(TripEnquiryStatuses.Asked, 2, 2)]);
        var stay = Row(rows, "stay:1");
        Assert.Equal(3, stay.NightsTotal);
        Assert.Equal(1, stay.NightsBooked);
        Assert.True(stay.ShortOfCover);
        Assert.False(TripEasyRows.IsRowDone(stay));
    }

    [Fact]
    public void M13_all_three_nights_confirmed_is_done()
    {
        var stay = Row(Rows(OneCity(), [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3)]), "stay:1");
        Assert.Equal(3, stay.NightsBooked);
        Assert.True(TripEasyRows.IsRowDone(stay));
    }

    [Fact]
    public void M13_only_asked_covers_no_night()
    {
        var stay = Row(Rows(OneCity(), [Stay(TripEnquiryStatuses.Asked, 1, 3)]), "stay:1");
        Assert.Equal(0, stay.NightsBooked);
        Assert.False(TripEasyRows.IsRowDone(stay));
    }

    // ---- M12: taxi coverage

    [Fact]
    public void M12_taxi_one_of_three_days_is_partial_not_done()
    {
        var taxi = Row(Rows(OneCity(), [Taxi(TripEnquiryStatuses.BookedByTraveller, 1)]), "taxi");
        Assert.True(taxi.ShortOfCover);
        Assert.False(TripEasyRows.IsRowDone(taxi));
    }

    [Fact]
    public void M12_taxi_all_days_over_two_rows_is_done()
    {
        var taxi = Row(Rows(OneCity(), [Taxi(TripEnquiryStatuses.BookedByTraveller, 1, 2), Taxi(TripEnquiryStatuses.ConfirmedByBoth, 3)]), "taxi");
        Assert.False(taxi.ShortOfCover);
        Assert.True(TripEasyRows.IsRowDone(taxi));
    }

    [Fact]
    public void M12_taxi_ask_only_covers_nothing()
    {
        var taxi = Row(Rows(OneCity(), [Taxi(TripEnquiryStatuses.Asked, 1, 2, 3), Taxi(TripEnquiryStatuses.FareSent, 1, 2, 3)]), "taxi");
        Assert.False(TripEasyRows.IsRowDone(taxi));
    }

    [Fact]
    public void M12_taxi_two_cities_one_covered_is_partial()
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs = [new TripCityLeg { Index = 1, CityKey = 1, CityName = "Nainital" }, new TripCityLeg { Index = 2, CityKey = 2, CityName = "Almora" }],
        };
        for (var d = 1; d <= 3; d++)
        {
            var day = new TripDay { Day = d, Leg = d <= 2 ? 1 : 2 };
            day.Spots.Add(new TripSpot { Id = $"s{d}", Name = "Spot", VisitMinutes = 60 });
            plan.Days.Add(day);
        }
        var taxi = Row(Rows(plan, [Taxi(TripEnquiryStatuses.ConfirmedByBoth, 1, 2)]), "taxi");
        Assert.True(taxi.ShortOfCover);
        Assert.False(TripEasyRows.IsRowDone(taxi));
    }

    // ---- M9: a confirmed stay is done at once (no advance step)

    [Fact]
    public void M9_confirmed_stay_is_done()
    {
        var stay = Row(Rows(OneCity(), [Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3)]), "stay:1");
        Assert.True(TripEasyRows.IsRowDone(stay));
        Assert.False(stay.NeedsYou);
    }

    [Fact]
    public void M9_confirmed_stay_makes_the_trip_ready()
    {
        var plan = OneCity();
        AllMarked(plan, Own);
        plan.Legs![0].StayMark = null;
        var confirmed = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, 3);
        Assert.Equal(TripBookingStates.Ready, TripBookingStates.Compute(TripOf(plan), plan, [confirmed]));
    }

    // ---- M14: all skipped is 0 of 0, not 100 percent and not ready

    [Fact]
    public void M14_all_skipped_is_100_percent_but_not_ready()
    {
        var plan = OneCity();
        AllMarked(plan, Skipped);
        var progress = TripEasyRows.Progress(1, Rows(plan));
        Assert.Equal(3, progress.Total);
        Assert.Equal(100, progress.Percent);
        Assert.Equal(TripBookingStates.PlanOnly, TripBookingStates.Compute(TripOf(plan), plan, []));
    }
}
