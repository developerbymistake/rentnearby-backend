using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

namespace RentNearBy.Tests.TaxiRequests;

public class MatcherSignalTests
{
    [Fact]
    public async Task Burst_of_marks_collapses_to_one_pending_city()
    {
        var signal = new MatcherSignal();
        signal.Reset();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => signal.MarkDirty(210000007))));

        Assert.Equal(1, signal.PendingCities);
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.Equal([210000007L], signal.TakeDirty());
        Assert.Empty(signal.TakeDirty());
    }

    [Fact]
    public void Held_back_city_stays_pending()
    {
        var signal = new MatcherSignal();
        signal.MarkDirty(1);
        signal.MarkDirty(2);

        Assert.Equal([2L], signal.TakeDirty(city => city == 1));
        Assert.True(signal.IsDirty(1));
    }

    [Fact]
    public void Mark_during_a_pass_is_taken_once_by_the_rerun()
    {
        var signal = new MatcherSignal();
        signal.MarkDirty(5);
        signal.TakeDirty();

        signal.MarkDirty(5);

        Assert.True(signal.TryTake(5));
        Assert.False(signal.TryTake(5));
    }

    [Fact]
    public async Task Freed_drivers_are_deduplicated_and_wake_the_matcher()
    {
        var signal = new MatcherSignal();
        signal.Reset();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        signal.MarkDriverFreed(a);
        signal.MarkDriverFreed(a);
        signal.MarkDriverFreed(b);

        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(new[] { a, b }.OrderBy(x => x), signal.TakeFreedDrivers().OrderBy(x => x));
        Assert.Empty(signal.TakeFreedDrivers());
    }
}
