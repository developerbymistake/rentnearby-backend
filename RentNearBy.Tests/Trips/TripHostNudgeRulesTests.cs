namespace RentNearBy.Tests.Trips;

public class TripHostNudgeRulesTests
{
    private static readonly DateTime Asked = new(2026, 11, 25, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ladders_are_2_12_24_and_1_4_8_near_the_trip()
    {
        Assert.Equal([2, 12, 24], TripHostNudgeRules.Ladder(Asked, Asked.AddHours(72)));
        Assert.Equal([1, 4, 8], TripHostNudgeRules.Ladder(Asked, Asked.AddHours(24)));
        Assert.Equal([1, 4, 8], TripHostNudgeRules.Ladder(Asked, Asked.AddHours(5)));
        Assert.Equal([2, 12, 24], TripHostNudgeRules.Ladder(Asked, Asked.AddHours(25)));
    }

    [Theory]
    [InlineData(1.9, 0)]
    [InlineData(2, 1)]
    [InlineData(11.9, 1)]
    [InlineData(12, 2)]
    [InlineData(24, 3)]
    [InlineData(40, 3)]
    public void Due_stage_on_the_normal_ladder(double hours, int stage)
        => Assert.Equal(stage, TripHostNudgeRules.DueStage(Asked, Asked.AddHours(72), Asked.AddHours(hours)));

    [Fact]
    public void A_night_that_swallowed_two_stages_gives_only_the_highest()
        => Assert.Equal(3, TripHostNudgeRules.DueStage(Asked, Asked.AddHours(72), Asked.AddHours(26)));

    [Fact]
    public void A_stage_after_the_ask_ends_never_fires()
    {
        // 10 h life: near-trip ladder 1, 4, 8.
        Assert.Equal(3, TripHostNudgeRules.DueStage(Asked, Asked.AddHours(10), Asked.AddHours(9)));
        // 6 h life: stage 3 (8 h) is after the end, so stage 2 stays the highest.
        Assert.Equal(2, TripHostNudgeRules.DueStage(Asked, Asked.AddHours(6), Asked.AddHours(9)));
    }

    [Fact]
    public void Keys_and_waiting_hours()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"hostnudge:{id}:2", TripHostNudgeRules.Key(id, 2));
        Assert.True(TripHostNudgeRules.Key(id, 3).Length <= 80);
        Assert.Equal(1, TripHostNudgeRules.WaitingHours(Asked, Asked.AddMinutes(20)));
        Assert.Equal(12, TripHostNudgeRules.WaitingHours(Asked, Asked.AddHours(12).AddMinutes(59)));
    }

    [Theory]
    [InlineData(16, 29, false)]  // 21:59 IST
    [InlineData(16, 30, true)]   // 22:00 IST
    [InlineData(20, 0, true)]    // 01:30 IST
    [InlineData(1, 29, true)]    // 06:59 IST
    [InlineData(1, 30, false)]   // 07:00 IST
    public void Quiet_hours_are_22_to_07_ist(int h, int m, bool night)
        => Assert.Equal(night, TripQuietHours.IsNight(new DateTime(2026, 11, 25, h, m, 0, DateTimeKind.Utc)));

    [Theory]
    [InlineData(17, 0, 26)]   // 22:30 IST -> next day 07:00 IST
    [InlineData(20, 0, 26)]   // 01:30 IST next day -> that day 07:00 IST
    [InlineData(0, 0, 25)]    // 05:30 IST -> same day 07:00 IST
    public void Held_pushes_go_at_the_next_07_ist(int h, int m, int day)
        => Assert.Equal(new DateTime(2026, 11, day, 1, 30, 0, DateTimeKind.Utc),
            TripQuietHours.NextMorningUtc(new DateTime(2026, 11, 25, h, m, 0, DateTimeKind.Utc)));

    [Fact]
    public void Traveller_keys()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"quiet:{id}", TripStayNudgeKeys.Quiet(id));
        Assert.Equal($"ended:{id}", TripStayNudgeKeys.Ended(id));
    }
}
