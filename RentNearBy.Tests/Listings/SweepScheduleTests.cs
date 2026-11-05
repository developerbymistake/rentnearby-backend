namespace RentNearBy.Tests.Listings;

public class SweepScheduleTests
{
    private static DateTime Utc(int h, int m, int s = 0) => new(2026, 11, 5, h, m, s, DateTimeKind.Utc);

    // Audit example: ValidUntil 10:16 UTC used to stay visible until the 11:15 sweep.
    [Fact]
    public void Wakes_just_after_the_next_valid_until()
    {
        var now = Utc(10, 15, 2);
        var anchor = SweepSchedule.NextHourlyAnchorUtc(now, 15);
        Assert.Equal(Utc(11, 15), anchor);
        Assert.Equal(Utc(10, 16, 1), SweepSchedule.NextWakeUtc(now, anchor, Utc(10, 16)));
    }

    [Fact]
    public void Anchor_rules_when_nothing_is_due_sooner()
    {
        var now = Utc(10, 0);
        Assert.Equal(Utc(10, 15), SweepSchedule.NextHourlyAnchorUtc(now, 15));
        Assert.Equal(Utc(10, 15), SweepSchedule.NextWakeUtc(now, Utc(10, 15), null));
        Assert.Equal(Utc(10, 15), SweepSchedule.NextWakeUtc(now, Utc(10, 15), Utc(12, 0)));
        Assert.Equal(Utc(11, 15), SweepSchedule.NextHourlyAnchorUtc(Utc(10, 15), 15));
    }

    [Fact]
    public void Never_sooner_than_the_minimum_gap()
    {
        var now = Utc(10, 0);
        Assert.Equal(now + SweepSchedule.MinGap, SweepSchedule.NextWakeUtc(now, Utc(10, 15), now));
        Assert.Equal(now + SweepSchedule.MinGap, SweepSchedule.NextWakeUtc(now, Utc(10, 15), now.AddMinutes(-3)));
    }
}
