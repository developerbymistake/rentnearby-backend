namespace RentNearBy.Tests.Trips;

// Owner rule: every day of a trip has a night; check-out is the morning after the last day.
// SendOffer, RevisedOffer, AcceptChange and ChangeRequest all check dates through TripDateRules.InsideTrip.
public class TripNightRuleTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly End = new(2026, 11, 3);

    private static DateOnly D(int day) => new(2026, 11, day);

    [Fact]
    public void CheckOutLimit_is_the_day_after_the_last_day()
        => Assert.Equal(D(4), TripDateRules.CheckOutLimit(End));

    [Theory]
    [InlineData(3, 4)]
    [InlineData(1, 4)]
    [InlineData(2, 3)]
    [InlineData(1, 2)]
    public void Send_offer_on_the_last_night_is_inside(int checkIn, int checkOut)
        => Assert.True(TripDateRules.InsideTrip(Start, End, D(checkIn), D(checkOut)));

    [Theory]
    [InlineData(3, 5)]
    [InlineData(4, 5)]
    public void Check_out_two_days_after_the_last_day_is_outside(int checkIn, int checkOut)
        => Assert.False(TripDateRules.InsideTrip(Start, End, D(checkIn), D(checkOut)));

    [Fact]
    public void Check_in_before_the_start_is_outside()
        => Assert.False(TripDateRules.InsideTrip(Start, End, new DateOnly(2026, 10, 31), D(2)));

    [Fact]
    public void Revised_offer_moving_to_the_last_night_is_inside()
        => Assert.True(TripDateRules.InsideTrip(Start, End, End, TripDateRules.CheckOutLimit(End)));

    [Fact]
    public void Accept_change_through_the_last_night_is_inside_and_one_more_night_is_not()
    {
        Assert.True(TripDateRules.InsideTrip(Start, End, D(2), D(4)));
        Assert.False(TripDateRules.InsideTrip(Start, End, D(2), D(5)));
    }

    [Fact]
    public void Change_request_for_every_night_is_inside()
        => Assert.True(TripDateRules.InsideTrip(Start, End, Start, D(4)));

    [Fact]
    public void One_day_trip_has_one_night()
    {
        Assert.True(TripDateRules.InsideTrip(Start, Start, Start, D(2)));
        Assert.False(TripDateRules.InsideTrip(Start, Start, Start, D(3)));
    }

    [Fact]
    public void Plan_check_out_matches_the_rule()
    {
        var plan = new TripPlan
        {
            Days = [new TripDay { Day = 1 }, new TripDay { Day = 2 }, new TripDay { Day = 3 }],
            Settings = new TripSettings { StartDate = Start },
        };
        Assert.Equal(End, plan.EndDate);
        Assert.Equal(TripDateRules.CheckOutLimit(End), plan.CheckOutDate);
    }

    [Fact]
    public void Outside_message_names_the_check_out_rule()
        => Assert.Equal("Offer dates must be inside the trip (check-out the morning after the last day).", TripDateRules.OutsideTripMessage);

    // Owner list: a confirmed booking whose check-out has passed lists under closed.
    [Theory]
    [InlineData("ConfirmedByBoth", 5, "confirmed")]
    [InlineData("ConfirmedByBoth", 4, "confirmed")]
    [InlineData("ConfirmedByBoth", 3, "closed")]
    [InlineData("Countered", 3, "new")]
    [InlineData("Asked", 3, "new")]
    [InlineData("Offered", 3, "offered")]
    [InlineData("Expired", 9, "closed")]
    public void Owner_bucket_moves_ended_confirmed_rows_to_closed(string status, int checkOutDay, string bucket)
        => Assert.Equal(bucket, TripOwnerBuckets.Of(status, D(checkOutDay), D(4)));

    [Fact]
    public void Owner_bucket_without_check_out_stays_confirmed()
        => Assert.Equal(TripOwnerBuckets.Confirmed, TripOwnerBuckets.Of(TripEnquiryStatuses.ConfirmedByBoth, null, D(4)));

    // ---- Change a confirmed stay: replacing one night of a booking keeps the other night booked

    private static TripEnquiry StayRow(string status, int fromDay, int toDay, int? total = null) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        Status = status,
        Day = (short)fromDay,
        CheckIn = D(fromDay),
        CheckOut = D(toDay + 1),
        AgreedTotal = total,
        AdvanceStatus = TripAdvanceStatuses.None,
    };

    [Fact]
    public void Replace_one_night_of_a_two_night_booking_keeps_the_other_night()
    {
        var booked = StayRow(TripEnquiryStatuses.ConfirmedByBoth, 1, 2, total: 4000);
        var split = TripReplaceSplit.Plan(booked, D(2), D(3), Guid.NewGuid(), DateTime.UtcNow);

        Assert.Same(booked, split.Kept);
        Assert.Equal(TripEnquiryStatuses.ConfirmedByBoth, split.Kept!.Status);
        Assert.Equal(D(1), split.Kept.CheckIn);
        Assert.Equal(D(2), split.Kept.CheckOut);
        Assert.Equal(TripEnquiryStatuses.CancelledByTraveller, split.Cancelled.Status);
        Assert.Equal(D(2), split.Cancelled.CheckIn);
        Assert.Equal(D(3), split.Cancelled.CheckOut);
        Assert.Equal(4000, split.Kept.AgreedTotal + split.Cancelled.AgreedTotal);
    }

    // ---- Journey rows are dated by their journey, never by the night window

    private static TripEnquiry JourneyRow(DateOnly date, string role) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = TripEnquiryStatuses.Asked,
        Day = TripExecutionRules.DayOf(Start, date),
        CheckIn = date,
        CheckOut = date.AddDays(1),
        ContextJson = $$"""{"city":"Nainital","journeyId":"{{Guid.NewGuid()}}","legIndex":1,"role":"{{role}}"}""",
    };

    [Fact]
    public void Coming_back_row_on_the_check_out_morning_is_not_outside_the_trip()
    {
        var row = JourneyRow(TripDateRules.CheckOutLimit(End), TripStepRoles.ComingBack);
        Assert.True(TripDateRules.OutsideWindow(Start, End, row.CheckIn, row.CheckOut));
        Assert.False(TripDateRules.RowOutsideWindow(row, Start, End));
    }

    [Fact]
    public void Getting_there_row_the_day_before_the_start_is_not_outside_the_trip()
        => Assert.False(TripDateRules.RowOutsideWindow(JourneyRow(Start.AddDays(-1), TripStepRoles.GettingThere), Start, End));

    [Fact]
    public void A_stay_row_on_the_check_out_morning_is_still_outside()
    {
        var stay = StayRow(TripEnquiryStatuses.Asked, 4, 4);
        Assert.True(TripDateRules.RowOutsideWindow(stay, Start, End));
    }
}
