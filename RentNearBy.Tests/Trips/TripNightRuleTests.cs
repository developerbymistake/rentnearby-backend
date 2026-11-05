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
    [InlineData("ConfirmedByTraveller", 3, "closed")]
    [InlineData("ConfirmedByTraveller", 4, "new")]
    [InlineData("Countered", 3, "new")]
    [InlineData("Asked", 3, "new")]
    [InlineData("Offered", 3, "offered")]
    [InlineData("Expired", 9, "closed")]
    public void Owner_bucket_moves_ended_confirmed_rows_to_closed(string status, int checkOutDay, string bucket)
        => Assert.Equal(bucket, TripOwnerBuckets.Of(status, D(checkOutDay), D(4)));

    [Fact]
    public void Owner_bucket_without_check_out_stays_confirmed()
        => Assert.Equal(TripOwnerBuckets.Confirmed, TripOwnerBuckets.Of(TripEnquiryStatuses.ConfirmedByBoth, null, D(4)));
}
