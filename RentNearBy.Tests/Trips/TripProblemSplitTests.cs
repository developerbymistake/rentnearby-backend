using System.Text.Json;

namespace RentNearBy.Tests.Trips;

public class TripProblemSplitTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateTime Now = new(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc);

    private static TripEnquiry Stay(int fromDay, int toDay, int? total = 3000) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        TravellerUserId = Guid.NewGuid(),
        OwnerUserId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        ListingId = Guid.NewGuid(),
        ListingName = "Deodar Homestay",
        ConversationId = Guid.NewGuid(),
        Day = (short)fromDay,
        CheckIn = Start.AddDays(fromDay - 1),
        CheckOut = Start.AddDays(toDay),
        Status = TripEnquiryStatuses.ConfirmedByBoth,
        AcceptedOfferId = Guid.NewGuid(),
        AdvanceStatus = TripAdvanceStatuses.Received,
        AgreedTotal = total,
        ExpiresAt = Now.AddDays(1),
    };

    private static TripEnquiry Taxi(string days, int? fare = 3000) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        TravellerUserId = Guid.NewGuid(),
        OwnerUserId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        ListingId = Guid.NewGuid(),
        ListingName = "Mohan Bisht",
        DaysJson = days,
        CheckIn = Start,
        CheckOut = Start.AddDays(3),
        Status = TripEnquiryStatuses.BookedByTraveller,
        AgreedFare = fare,
        ExpiresAt = Now.AddDays(1),
    };

    private static int[] DaysOf(TripEnquiry e) => JsonSerializer.Deserialize<int[]>(e.DaysJson!)!;

    [Fact]
    public void Middle_night_keeps_the_nights_already_slept()
    {
        var e = Stay(1, 3);
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);

        Assert.Same(e, kept);
        Assert.Equal(Start.AddDays(1), kept!.CheckOut);
        Assert.Equal(TripEnquiryStatuses.ConfirmedByBoth, kept.Status);
        Assert.Equal(1000, kept.AgreedTotal);

        Assert.NotSame(e, failed);
        Assert.NotEqual(e.Id, failed.Id);
        Assert.Equal(TripEnquiryStatuses.Failed, failed.Status);
        Assert.Equal(TripFailReasons.ProblemHostNoRoom, failed.FailReason);
        Assert.Equal(Start.AddDays(1), failed.CheckIn);
        Assert.Equal(Start.AddDays(3), failed.CheckOut);
        Assert.Equal((short)2, failed.Day);
        Assert.Equal(2000, failed.AgreedTotal);
        Assert.Null(failed.AcceptedOfferId);
        Assert.Equal(TripAdvanceStatuses.None, failed.AdvanceStatus);
        Assert.Equal(e.ConversationId, failed.ConversationId);
        Assert.Equal(e.ListingId, failed.ListingId);
        Assert.Equal(e.TripId, failed.TripId);
    }

    [Fact]
    public void Split_totals_add_up_when_the_share_rounds()
    {
        var e = Stay(1, 3, total: 1001);
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);
        Assert.Equal(1001, kept!.AgreedTotal + failed.AgreedTotal);
    }

    [Fact]
    public void First_night_fails_the_whole_row()
    {
        var e = Stay(1, 3);
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 1, Now);

        Assert.Null(kept);
        Assert.Same(e, failed);
        Assert.Equal(TripEnquiryStatuses.Failed, e.Status);
        Assert.Equal(TripFailReasons.ProblemHostNoRoom, e.FailReason);
        Assert.Equal(Start.AddDays(3), e.CheckOut);
        Assert.Equal(3000, e.AgreedTotal);
    }

    [Fact]
    public void Single_night_row_fails_whole()
    {
        var e = Stay(2, 2);
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);
        Assert.Null(kept);
        Assert.Same(e, failed);
    }

    [Fact]
    public void Last_night_of_three_splits_two_and_one()
    {
        var e = Stay(1, 3);
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 3, Now);
        Assert.Equal(Start.AddDays(2), kept!.CheckOut);
        Assert.Equal(2000, kept.AgreedTotal);
        Assert.Equal(Start.AddDays(2), failed.CheckIn);
        Assert.Equal(1000, failed.AgreedTotal);
    }

    [Fact]
    public void A_problem_after_check_out_affects_nothing()
    {
        var e = Stay(1, 2);
        Assert.False(TripProblemSplit.Affects(e, Start, 3));
        Assert.True(TripProblemSplit.Affects(e, Start, 2));
        Assert.True(TripProblemSplit.Affects(e, Start, 1));
    }

    [Fact]
    public void Taxi_days_split_by_day_number()
    {
        var e = Taxi("[1,2,3]", fare: 3000);
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);

        Assert.Same(e, kept);
        Assert.Equal(new[] { 1 }, DaysOf(kept!));
        Assert.Equal(Start.AddDays(1), kept.CheckOut);
        Assert.Equal(1000, kept.AgreedFare);

        Assert.Equal(new[] { 2, 3 }, DaysOf(failed));
        Assert.Equal(Start.AddDays(1), failed.CheckIn);
        Assert.Equal(Start.AddDays(3), failed.CheckOut);
        Assert.Equal(2000, failed.AgreedFare);
        Assert.Equal(TripEnquiryStatuses.Failed, failed.Status);
        Assert.Equal(TripFailReasons.ProblemDriverNoShow, failed.FailReason);
    }

    [Fact]
    public void Taxi_first_day_fails_the_whole_row()
    {
        var e = Taxi("[1,2,3]");
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 1, Now);
        Assert.Null(kept);
        Assert.Same(e, failed);
        Assert.Equal(TripFailReasons.ProblemDriverNoShow, e.FailReason);
        Assert.Equal(new[] { 1, 2, 3 }, DaysOf(e));
    }

    [Fact]
    public void Taxi_with_a_gap_starts_the_failed_row_on_its_own_first_day()
    {
        var e = Taxi("[1,3]");
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);
        Assert.Equal(new[] { 1 }, DaysOf(kept!));
        Assert.Equal(new[] { 3 }, DaysOf(failed));
        Assert.Equal(Start.AddDays(2), failed.CheckIn);
        Assert.Equal(Start.AddDays(3), failed.CheckOut);
    }

    [Fact]
    public void Taxi_booked_before_a_start_date_move_is_split_by_date()
    {
        var e = Taxi("[1,2,3]");
        var moved = Start.AddDays(1);
        // Plan day 1 is now Nov 2: the row covers plan days 0 (gone), 1 and 2.
        Assert.True(TripProblemSplit.Affects(e, moved, 2));
        var (kept, failed) = TripProblemSplit.Plan(e, moved, 2, Now);
        Assert.Equal(new[] { 1, 2 }, DaysOf(kept!));
        Assert.Equal(new[] { 3 }, DaysOf(failed));
        Assert.Equal(Start.AddDays(2), failed.CheckIn);
    }

    [Fact]
    public void Journey_car_on_its_travel_day_fails_whole()
    {
        var e = Taxi("[2]", fare: 4200);
        e.Day = 2;
        e.CheckIn = Start.AddDays(1);
        e.CheckOut = Start.AddDays(2);
        e.ContextJson = $$"""{"city":"Badrinath","journeyId":"{{Guid.NewGuid()}}","legIndex":1}""";
        Assert.True(TripSegmentRows.IsSegment(e));
        Assert.True(TripProblemSplit.Affects(e, Start, 2));

        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);
        Assert.Null(kept);
        Assert.Same(e, failed);
        Assert.Equal(TripEnquiryStatuses.Failed, e.Status);
        Assert.Equal(TripFailReasons.ProblemDriverNoShow, e.FailReason);
        Assert.Equal(4200, e.AgreedFare);
    }

    [Fact]
    public void Taxi_problem_after_the_last_day_affects_nothing()
    {
        var e = Taxi("[1,2]");
        Assert.False(TripProblemSplit.Affects(e, Start, 3));
    }
}
