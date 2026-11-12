using RentNearBy.Infrastructure.Services.Retention;

namespace RentNearBy.Tests.Retention;

public class RetentionSafetyGuardTests
{
    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(5_000_000, true)]
    public void Guard_applies_only_from_the_minimum_row_count(long rows, bool applies)
        => Assert.Equal(applies, RetentionSafetyGuard.Applies(rows, 1000, false, false));

    [Fact]
    public void Exempt_rules_and_the_backlog_escape_skip_the_guard()
    {
        Assert.False(RetentionSafetyGuard.Applies(1_000_000, 1000, true, false));
        Assert.False(RetentionSafetyGuard.Applies(1_000_000, 1000, false, true));
    }

    [Theory]
    [InlineData(10_000, 20, 2000)]
    [InlineData(1000, 20, 200)]
    [InlineData(1001, 50, 500)]
    public void Cap_is_a_percent_of_the_table(long rows, int pct, long cap)
        => Assert.Equal(cap, RetentionSafetyGuard.Cap(rows, pct));

    [Fact]
    public void Cap_itself_is_allowed_and_one_more_stops()
    {
        Assert.False(RetentionSafetyGuard.IsOverCap(2000, 2000));
        Assert.True(RetentionSafetyGuard.IsOverCap(2001, 2000));
        Assert.False(RetentionSafetyGuard.IsOverCap(0, 0));
    }
}
