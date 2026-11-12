using RentNearBy.Api.Handlers;
using RentNearBy.Core.Entities;

namespace RentNearBy.Tests.Trips;

// Journey integrity, owner C: shared guards, send results, replay and step rules (pure parts; the DB paths skip without PostgreSQL).
public class TripGuardsTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);

    private static TripPlan TwoCityPlan() => new()
    {
        Legs = [new TripCityLeg { Index = 1, CityName = "A" }, new TripCityLeg { Index = 2, CityName = "B" }],
        Days = [new TripDay { Day = 1, Leg = 1 }, new TripDay { Day = 2, Leg = 1 }, new TripDay { Day = 3, Leg = 2 }],
    };

    private static TripEnquiry Row(Guid trip, string kind, string? step, string status = TripEnquiryStatuses.Asked, double ageHours = 1)
        => new() { Id = Guid.NewGuid(), TripId = trip, Kind = kind, StepKey = step, Status = status, ListingId = Guid.NewGuid(), CreatedAt = Now.AddHours(-ageHours) };

    // ----- results -----

    [Fact]
    public void Send_status_for_partial_cover_and_booked_driver_is_truthful()
    {
        Assert.Equal("OpenForOtherNights", TripSendStatuses.FromSkip(TripSkipReasons.OpenForOtherNights));
        Assert.Equal("AlreadyBooked", TripSendStatuses.FromSkip(TripSkipReasons.AlreadyBooked));
        Assert.Equal("AlreadyOpen", TripSendStatuses.FromSkip(TripSkipReasons.AlreadyOpen));
    }

    [Fact]
    public void Results_never_say_Sent_for_a_skipped_host()
    {
        var id = Guid.NewGuid();
        var results = TripBulk.BuildResults([id], new HashSet<Guid>(), new List<Guid>(),
            [(id, TripSkipReasons.OpenForOtherNights)], [], null);
        Assert.Equal("OpenForOtherNights", results.Single().Status);
        Assert.NotEqual("Sent", results.Single().Status);
    }

    // ----- replay (B6b) -----

    [Fact]
    public void Replay_of_an_open_row_of_the_same_trip_step_and_kind_counts()
    {
        var trip = Guid.NewGuid();
        var row = Row(trip, TripEnquiryKinds.Stay, "stay:1");
        var (replay, stale) = TripGuards.SplitReplay([row], trip, TripEnquiryKinds.Stay, "stay:1", Now);
        Assert.Single(replay);
        Assert.Empty(stale);
    }

    [Theory]
    [InlineData("otherTrip")]
    [InlineData("otherStep")]
    [InlineData("otherKind")]
    [InlineData("withdrawn")]
    [InlineData("old")]
    public void Replay_of_any_other_row_is_never_replayed(string why)
    {
        var trip = Guid.NewGuid();
        var row = why switch
        {
            "otherTrip" => Row(Guid.NewGuid(), TripEnquiryKinds.Stay, "stay:1"),
            "otherStep" => Row(trip, TripEnquiryKinds.Stay, "stay:2"),
            "otherKind" => Row(trip, TripEnquiryKinds.Taxi, "stay:1"),
            "withdrawn" => Row(trip, TripEnquiryKinds.Stay, "stay:1", TripEnquiryStatuses.Closed),
            _ => Row(trip, TripEnquiryKinds.Stay, "stay:1", ageHours: 25),
        };
        var (replay, stale) = TripGuards.SplitReplay([row], trip, TripEnquiryKinds.Stay, "stay:1", Now);
        Assert.Empty(replay);
        Assert.Contains(row.ListingId, stale);
    }

    // ----- step key vs kind vs day (M2) -----

    [Fact]
    public void Real_steps_pass()
    {
        var plan = TwoCityPlan();
        Assert.Null(TripGuards.StepMismatch(plan, "stay:1", TripShortlistKinds.Stay, 1));
        Assert.Null(TripGuards.StepMismatch(plan, "stay:2", TripShortlistKinds.Stay, 3));
        Assert.Null(TripGuards.StepMismatch(plan, "taxi", TripShortlistKinds.Taxi));
        Assert.Null(TripGuards.StepMismatch(plan, "gettingThere", TripShortlistKinds.Car));
        Assert.Null(TripGuards.StepMismatch(plan, "between:2", TripShortlistKinds.Car));
    }

    [Fact]
    public void Taxi_under_a_stay_step_is_a_mismatch() => Assert.NotNull(TripGuards.StepMismatch(TwoCityPlan(), "stay:1", TripShortlistKinds.Taxi));

    [Fact]
    public void Stay_under_a_travel_step_is_a_mismatch() => Assert.NotNull(TripGuards.StepMismatch(TwoCityPlan(), "gettingThere", TripShortlistKinds.Stay));

    [Fact]
    public void Stay_step_of_another_city_than_the_day_is_a_mismatch()
    {
        Assert.NotNull(TripGuards.StepMismatch(TwoCityPlan(), "stay:2", TripShortlistKinds.Stay, 1));
        Assert.NotNull(TripGuards.StepMismatch(TwoCityPlan(), "stay:1", TripShortlistKinds.Stay, 3));
    }

    [Fact]
    public void Step_not_in_the_plan_is_a_mismatch()
    {
        Assert.NotNull(TripGuards.StepMismatch(TwoCityPlan(), "stay:9", TripShortlistKinds.Stay));
        Assert.NotNull(TripGuards.StepMismatch(TwoCityPlan(), "between:3", TripShortlistKinds.Car));
        Assert.NotNull(TripGuards.StepMismatch(TwoCityPlan(), null, TripShortlistKinds.Stay));
    }

    // ----- closed step -----

    [Fact]
    public void Own_or_skipped_steps_are_closed()
    {
        var plan = TwoCityPlan();
        Assert.False(TripGuards.StepClosed(plan, "taxi"));
        plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        plan.Legs![1].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
        plan.Guide = new TripGuide { Choices = new() { ["stay:1"] = TripGuideChoices.Skip, ["between:2"] = TripGuideChoices.Find } };
        Assert.True(TripGuards.StepClosed(plan, "taxi"));
        Assert.True(TripGuards.StepClosed(plan, "stay:2"));
        Assert.True(TripGuards.StepClosed(plan, "stay:1"));
        Assert.False(TripGuards.StepClosed(plan, "between:2"));
    }

    // ----- cap, nights, completed, listing -----

    [Theory]
    [InlineData(0, 3, true)]
    [InlineData(2, 1, true)]
    [InlineData(3, 1, false)]
    [InlineData(2, 2, false)]
    public void Step_cap_counts_open_rows_and_new_rows(int open, int added, bool fits)
        => Assert.Equal(fits, TripGuards.StepHasRoomFor(open, added));

    [Fact]
    public void Open_row_for_fewer_nights_does_not_cover_the_request()
    {
        var row = new TripEnquiry { CheckIn = new DateOnly(2026, 12, 1), CheckOut = new DateOnly(2026, 12, 3) };
        Assert.True(TripGuards.CoversNights(row, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 3)));
        Assert.True(TripGuards.CoversNights(row, new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 3)));
        Assert.False(TripGuards.CoversNights(row, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 4)));
    }

    [Fact]
    public void Completed_trip_is_refused_and_a_live_trip_is_not()
    {
        Assert.NotNull(TripGuards.TripNotCompleted(new Trip { Status = TripStatuses.Completed }));
        Assert.Null(TripGuards.TripNotCompleted(new Trip { Status = TripStatuses.Planned }));
    }

    [Theory]
    [InlineData(false, StayStatuses.Approved, true)]
    [InlineData(true, StayStatuses.Approved, false)]
    [InlineData(false, StayStatuses.Rejected, false)]
    public void Listing_is_live_only_when_approved_and_not_deleted(bool deleted, string status, bool live)
        => Assert.Equal(live, TripGuards.ListingLive(deleted, status, StayStatuses.Approved));
}
