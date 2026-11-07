using System.Text.Json;

namespace RentNearBy.Tests.Trips;

public class TripRound2RulesTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateTime Now = new(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Merge_into_a_replied_lead_resets_it_to_asked()
    {
        var row = new TripEnquiry
        {
            Status = TripEnquiryStatuses.FareSent,
            DriverFare = 2000,
            DriverNote = "AC car",
            DriverRepliedAt = Now,
            DriverFareEditCount = 2,
        };

        TripLeadRules.ResetForMerge(row);

        Assert.Equal(TripEnquiryStatuses.Asked, row.Status);
        Assert.Null(row.DriverFare);
        Assert.Null(row.DriverNote);
        Assert.Null(row.DriverRepliedAt);
        Assert.Equal(0, row.DriverFareEditCount);
    }

    [Fact]
    public void Edit_on_day_two_keeps_day_one()
    {
        var today = Start.AddDays(1);
        var kept = TripLeadRules.KeptPastDays([1, 2, 3], Start, Start, 3, today);
        Assert.Equal(new List<int> { 1 }, kept);
    }

    [Fact]
    public void Kept_past_days_follow_the_dates_after_a_start_change()
    {
        // Booked for 01-03 Nov as days 1..3; the trip now starts 31 Oct, so 01 Nov is day 2.
        var kept = TripLeadRules.KeptPastDays([1, 2, 3], Start, Start.AddDays(-1), 4, Start.AddDays(1));
        Assert.Equal(new List<int> { 2 }, kept);
    }

    [Fact]
    public void Nothing_kept_before_the_trip_starts()
    {
        Assert.Empty(TripLeadRules.KeptPastDays([1, 2], Start, Start, 2, Start));
        Assert.Empty(TripLeadRules.KeptPastDays(null, Start, Start, 2, Start.AddDays(5)));
    }

    [Fact]
    public void Outside_window_tests_the_whole_range_with_the_night_rule()
    {
        var end = Start.AddDays(2);
        Assert.False(TripDateRules.OutsideWindow(Start, end, Start, end.AddDays(1)));
        Assert.True(TripDateRules.OutsideWindow(Start.AddDays(1), end, Start, Start.AddDays(2)));
        Assert.True(TripDateRules.OutsideWindow(Start, end, end, end.AddDays(2)));
        Assert.True(TripDateRules.OutsideWindow(null, null, Start, Start.AddDays(1)));
    }

    [Fact]
    public void Day_follows_check_in()
    {
        Assert.Equal((short)1, TripExecutionRules.DayOf(Start, Start));
        Assert.Equal((short)3, TripExecutionRules.DayOf(Start, Start.AddDays(2)));
        Assert.Equal(Start.AddDays(2), TripExecutionRules.NightDate(Start, TripExecutionRules.DayOf(Start, Start.AddDays(2))));
    }

    [Fact]
    public void Split_rewrites_the_accepted_terms_for_the_kept_nights()
    {
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(),
            TripId = Guid.NewGuid(),
            Kind = TripEnquiryKinds.Stay,
            Day = 1,
            CheckIn = Start,
            CheckOut = Start.AddDays(3),
            Status = TripEnquiryStatuses.ConfirmedByBoth,
            AgreedTotal = 9000,
        };
        var accepted = new TripOffer
        {
            Id = Guid.NewGuid(),
            TripEnquiryId = e.Id,
            Kind = TripOfferKinds.Offer,
            CheckIn = Start,
            CheckOut = Start.AddDays(3),
            LinesJson = "[{\"units\":1,\"guests\":2,\"pricePerNight\":3000}]",
            Nights = 3,
            Total = 9000,
            CancellationText = "Free till 2 days before",
            Status = TripOfferStatuses.Accepted,
        };

        var (kept, _) = TripProblemSplit.Plan(e, Start, 2, Now);
        var shrunk = TripProblemSplit.ShrinkAcceptedOffer(accepted, kept!, Now);

        Assert.NotEqual(accepted.Id, shrunk.Id);
        Assert.Equal(e.Id, shrunk.TripEnquiryId);
        Assert.Equal(Start, shrunk.CheckIn);
        Assert.Equal(Start.AddDays(1), shrunk.CheckOut);
        Assert.Equal((short)1, shrunk.Nights);
        Assert.Equal(3000, shrunk.Total);
        Assert.Equal(TripOfferStatuses.Accepted, shrunk.Status);
        Assert.Equal(accepted.LinesJson, shrunk.LinesJson);
        Assert.Equal(accepted.CancellationText, shrunk.CancellationText);
    }

    [Fact]
    public void Booking_quota_is_its_own()
    {
        Assert.Equal(20, TripLeadRules.BookingsPerHour);
        Assert.Equal("TripDatesChanged", TripCloseReasons.TripDatesChanged);
        Assert.NotEmpty(TripSystemLines.Text(TripSystemLines.TripDatesChanged));
    }
}
