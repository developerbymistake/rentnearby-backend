using RentNearBy.Infrastructure.Services.Retention;

namespace RentNearBy.Tests.Retention;

public class RetentionBusyGuardTests
{
    private static readonly RetentionOptions O = new();

    [Theory]
    [InlineData(0, RetentionLoad.Normal)]
    [InlineData(7, RetentionLoad.Normal)]
    [InlineData(8, RetentionLoad.Busy)]
    [InlineData(14, RetentionLoad.Busy)]
    [InlineData(15, RetentionLoad.Heavy)]
    public void Load_is_classified_by_active_sessions(int active, RetentionLoad load)
        => Assert.Equal(load, RetentionBusyGuard.Classify(active, O));

    [Fact]
    public void Slow_batch_halves_down_to_the_floor()
    {
        Assert.Equal(500, RetentionBusyGuard.NextBatch(1000, 2000, O));
        Assert.Equal(100, RetentionBusyGuard.NextBatch(150, 2000, O));
        Assert.Equal(100, RetentionBusyGuard.NextBatch(100, 2000, O));
    }

    [Fact]
    public void Fast_batch_grows_back_to_the_ceiling()
    {
        Assert.Equal(200, RetentionBusyGuard.NextBatch(100, 50, O));
        Assert.Equal(1000, RetentionBusyGuard.NextBatch(800, 50, O));
    }

    [Fact]
    public void Medium_batch_keeps_its_size()
        => Assert.Equal(400, RetentionBusyGuard.NextBatch(400, 800, O));

    [Fact]
    public void Pause_doubles_when_slow_up_to_five_seconds_and_resets_when_fast()
    {
        Assert.Equal(600, RetentionBusyGuard.NextPause(300, 2000, O));
        Assert.Equal(5000, RetentionBusyGuard.NextPause(4000, 2000, O));
        Assert.Equal(300, RetentionBusyGuard.NextPause(2400, 100, O));
        Assert.Equal(700, RetentionBusyGuard.NextPause(700, 800, O));
    }

    [Fact]
    public void Busy_or_heavy_load_uses_the_minimum_batch()
    {
        Assert.Equal(1000, RetentionBusyGuard.BatchFor(RetentionLoad.Normal, 1000, O));
        Assert.Equal(100, RetentionBusyGuard.BatchFor(RetentionLoad.Busy, 1000, O));
    }
}
