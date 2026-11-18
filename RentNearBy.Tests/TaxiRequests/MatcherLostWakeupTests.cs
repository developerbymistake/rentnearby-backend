using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

namespace RentNearBy.Tests.TaxiRequests;

public class MatcherLostWakeupTests
{
    private const long City = 210000005;
    private static readonly DateTime T0 = new(2026, 12, 18, 6, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Loser_of_the_try_lock_marks_the_city_dirty_and_the_holder_reruns()
    {
        var h = new Harness();
        var barrier = new FirstCallBarrier();
        var service = h.Service(new TaxiMatcherPass(h.Units, h.Feature, h.Signal, barrier));
        h.Signal.MarkDirty(City);

        var drain = service.RunOnceAsync(Ct);
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Another instance's pass for the same city while the holder sits between its lock and its commit.
        var loser = await new TaxiMatcherPass(h.Units, h.Feature, h.Signal).RunAsync(City, Ct);
        Assert.Equal(MatcherPassOutcome.LockBusy, loser.Outcome);
        Assert.True(h.Signal.IsDirty(City));

        barrier.Release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, h.Units.Commits);
        Assert.False(h.Signal.IsDirty(City));
        Assert.Empty(h.Units.Held);
    }

    [Fact]
    public async Task Sweeper_finds_a_city_missed_by_signals()
    {
        var h = new Harness();
        h.Sweep.Due = [City];
        var pass = new RecordingPass();
        var service = h.Service(pass);

        await service.RunOnceAsync(Ct);

        Assert.Equal([City], pass.Cities);
        Assert.Equal(0, h.Signal.PendingCities);

        h.Clock.Now += TimeSpan.FromSeconds(1);
        await service.RunOnceAsync(Ct);
        Assert.Equal(1, h.Sweep.DueCalls);

        h.Clock.Now += TimeSpan.FromSeconds(2);
        await service.RunOnceAsync(Ct);
        Assert.Equal(2, h.Sweep.DueCalls);
        Assert.Equal([City, City], pass.Cities);
    }

    [Fact]
    public async Task Switch_off_creates_no_offers()
    {
        var h = new Harness();
        h.Feature.Enabled = false;

        var result = await new TaxiMatcherPass(h.Units, h.Feature, h.Signal).RunAsync(City, Ct);

        Assert.Equal(MatcherPassOutcome.SwitchOff, result.Outcome);
        Assert.Equal(0, h.Units.Loads);
        Assert.Equal(0, h.Units.Offers);
        Assert.Equal(0, h.Units.Commits);
        Assert.Empty(h.Units.Held);

        var pass = new RecordingPass();
        var service = h.Service(pass);
        h.Sweep.Due = [City];
        h.Signal.MarkDirty(City);
        await service.RunOnceAsync(Ct);

        Assert.Empty(pass.Cities);
        Assert.Equal(0, h.Sweep.DueCalls);
    }

    [Fact]
    public async Task Switch_on_pass_rings_the_free_candidate()
    {
        var h = new Harness();

        var result = await new TaxiMatcherPass(h.Units, h.Feature, h.Signal).RunAsync(City, Ct);

        Assert.Equal(MatcherPassOutcome.Done, result.Outcome);
        Assert.Equal(1, h.Units.Offers);
        Assert.Equal(1, h.Units.Commits);
    }

    [Fact]
    public async Task A_failing_pass_is_logged_and_never_escapes_the_service()
    {
        var h = new Harness();
        var pass = new RecordingPass { Throw = true };
        var logger = new ListLogger();
        var service = h.Service(pass, logger);
        h.Signal.MarkDirty(City);

        await service.RunOnceAsync(Ct);

        Assert.Contains(logger.Events, e => e == TaxiRequestLogEvents.MatchPassFailed.Id);
        Assert.True(h.Signal.IsDirty(City));

        // Held back after a failure: the next drain does not hammer the city at once.
        await service.RunOnceAsync(Ct);
        Assert.Single(pass.Cities);
    }

    [Fact]
    public async Task Lock_timeout_in_a_write_ends_the_pass_as_lock_busy()
    {
        var h = new Harness();
        h.Units.ApplyThrows = new PostgresException("canceling statement due to lock timeout", "ERROR", "ERROR", TaxiMatcherPass.LockNotAvailable);

        var result = await new TaxiMatcherPass(h.Units, h.Feature, h.Signal).RunAsync(City, Ct);

        Assert.Equal(MatcherPassOutcome.LockBusy, result.Outcome);
        Assert.True(h.Signal.IsDirty(City));
        Assert.Equal(0, h.Units.Commits);
        Assert.Equal(1, h.Units.LockTries);
        Assert.Empty(h.Units.Held);
    }

    [Fact]
    public void Lock_timeout_wrapped_by_ef_is_still_lock_busy()
    {
        var pg = new PostgresException("lock timeout", "ERROR", "ERROR", TaxiMatcherPass.LockNotAvailable);
        Assert.True(TaxiMatcherPass.IsLockTimeout(new InvalidOperationException("wrapped", pg)));
        Assert.False(TaxiMatcherPass.IsLockTimeout(new PostgresException("other", "ERROR", "ERROR", "23505")));
        Assert.False(TaxiMatcherPass.IsLockTimeout(new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task Lock_timeout_pass_is_not_logged_as_failed_and_is_held_back_briefly()
    {
        var h = new Harness();
        h.Units.ApplyThrows = new PostgresException("lock timeout", "ERROR", "ERROR", TaxiMatcherPass.LockNotAvailable);
        var logger = new ListLogger();
        var service = h.Service(new TaxiMatcherPass(h.Units, h.Feature, h.Signal), logger);
        h.Signal.MarkDirty(City);

        await service.RunOnceAsync(Ct);

        Assert.DoesNotContain(logger.Events, e => e == TaxiRequestLogEvents.MatchPassFailed.Id);
        Assert.True(h.Signal.IsDirty(City));

        h.Units.ApplyThrows = null;
        h.Clock.Now += MatcherServiceTimings.Default.LockBusyBackoff;
        await service.RunOnceAsync(Ct);
        Assert.Equal(1, h.Units.Commits);
    }

    [Fact]
    public void Write_lock_wait_is_bounded_far_below_the_failed_pass_backoff()
    {
        Assert.True(MatcherSql.LockTimeoutMs <= MatcherServiceTimings.Default.LockBusyBackoff.TotalMilliseconds);
        Assert.Equal($"SET LOCAL lock_timeout = '{MatcherSql.LockTimeoutMs}ms'", MatcherSql.SetLockTimeout);
    }

    [Fact]
    public async Task Lock_busy_city_is_held_back_not_spun()
    {
        var h = new Harness();
        h.Units.Held.TryAdd(City, 0);
        var service = h.Service(new TaxiMatcherPass(h.Units, h.Feature, h.Signal));
        h.Signal.MarkDirty(City);

        await service.RunOnceAsync(Ct);
        await service.RunOnceAsync(Ct);

        Assert.Equal(1, h.Units.LockTries);
        Assert.True(h.Signal.IsDirty(City));
    }

    [Fact]
    public void Input_excludes_non_candidates_and_flags_hidden_candidates()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var hiddenDriver = Guid.NewGuid();
        var r1 = Request(Guid.NewGuid());
        var r2 = Request(Guid.NewGuid());
        var data = new MatcherCityData(City, T0, new FairQueueCity(0, 0), [r1, r2],
            [Pool(a, free: true), Pool(b, free: false)],
            [new TaxiCandidatePair(r1.Id, a, true), new TaxiCandidatePair(r2.Id, b, false), new TaxiCandidatePair(r2.Id, hiddenDriver, true)],
            false);

        var input = MatcherInput.Build(data);
        var pick = TaxiFairQueue.Pick(input.Pick, T0);

        Assert.Equal([r2.Id], input.HiddenCandidates);
        var offer = Assert.Single(pick.Offers);
        Assert.Equal((r1.Id, a), (offer.RequestId, offer.DriverListingId));
        Assert.Equal(FairQueueOutcome.Wait, pick.Requests.Single(r => r.RequestId == r2.Id).Outcome);
    }

    private static MatcherRequestRow Request(Guid id) => new(id, Guid.NewGuid(), Guid.NewGuid(), T0, 3, 0, 0, 4, City, null,
        new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 22), null, null);

    private static TaxiPoolDriver Pool(Guid id, bool free) => new(id, Guid.NewGuid(), 4, 0, 0, null, free);

    private sealed class Harness
    {
        public MatcherSignal Signal { get; } = new();
        public FakeFeature Feature { get; } = new();
        public FakeUnits Units { get; } = new();
        public FakeSweep Sweep { get; } = new();
        public FakeClock Clock { get; } = new();

        public TaxiMatcherService Service(ITaxiMatcherPass pass, ILogger? logger = null)
            => new(pass, Sweep, Signal, Feature, logger ?? new ListLogger(), new WorkerHeartbeats(), Clock, MatcherServiceTimings.Default);
    }

    private sealed class FakeFeature : ITripExecutionFeatureReader
    {
        public bool Enabled { get; set; } = true;
        public Task<bool> IsEnabledAsync() => Task.FromResult(Enabled);
        public void Invalidate() { }
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(T0);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSweep : IMatcherSweepSource
    {
        public IReadOnlyList<long> Due { get; set; } = [];
        public int DueCalls { get; private set; }

        public Task<IReadOnlyList<long>> DueCitiesAsync(CancellationToken ct)
        {
            DueCalls++;
            return Task.FromResult(Due);
        }

        public Task<IReadOnlyList<long>> CitiesOfDriversAsync(IReadOnlyCollection<Guid> driverListingIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<long>>([]);

        public Task<int> CountStuckAsync(CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class RecordingPass : ITaxiMatcherPass
    {
        public ConcurrentQueue<long> Queue { get; } = new();
        public bool Throw { get; set; }
        public long[] Cities => Queue.ToArray();

        public Task<MatcherPassResult> RunAsync(long cityKey, CancellationToken ct)
        {
            Queue.Enqueue(cityKey);
            if (Throw) throw new InvalidOperationException("boom");
            return Task.FromResult(new MatcherPassResult(MatcherPassOutcome.Done));
        }
    }

    private sealed class FirstCallBarrier : IMatcherBarrier
    {
        private int _calls;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReachedAsync(long cityKey, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) > 1) return Task.CompletedTask;
            Reached.SetResult();
            return Release.Task;
        }
    }

    // Transaction-scoped try lock like pg_try_advisory_xact_lock: held until the unit is disposed.
    private sealed class FakeUnits : IMatcherPassUnitFactory
    {
        public ConcurrentDictionary<long, byte> Held { get; } = new();
        public int Loads, Offers, Commits, LockTries;
        public Exception? ApplyThrows;

        public Task<IMatcherPassUnit> BeginAsync(CancellationToken ct) => Task.FromResult<IMatcherPassUnit>(new Unit(this));

        private sealed class Unit(FakeUnits owner) : IMatcherPassUnit
        {
            private long? _locked;

            public Task<bool> TryLockCityAsync(long cityKey, CancellationToken ct)
            {
                Interlocked.Increment(ref owner.LockTries);
                if (!owner.Held.TryAdd(cityKey, 0)) return Task.FromResult(false);
                _locked = cityKey;
                return Task.FromResult(true);
            }

            public Task<MatcherCityData?> LoadAsync(long cityKey, CancellationToken ct)
            {
                Interlocked.Increment(ref owner.Loads);
                var driver = Guid.NewGuid();
                var request = Request(Guid.NewGuid());
                return Task.FromResult<MatcherCityData?>(new MatcherCityData(cityKey, T0, new FairQueueCity(0, 0), [request],
                    [Pool(driver, free: true)], [new TaxiCandidatePair(request.Id, driver, true)], false));
            }

            public Task<MatcherApplyResult> ApplyAsync(MatcherCityData data, MatcherInput input, PickResult pick, CancellationToken ct)
            {
                if (owner.ApplyThrows is { } ex) throw ex;
                Interlocked.Add(ref owner.Offers, pick.Offers.Count);
                return Task.FromResult(new MatcherApplyResult(pick.Offers.Count, false, TaxiClosureResult.Empty));
            }

            public Task CommitAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref owner.Commits);
                return Task.CompletedTask;
            }

            public Task AfterCommitAsync(TaxiClosureResult signals) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                if (_locked is { } city) owner.Held.TryRemove(city, out _);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<int> Queue { get; } = new();
        public int[] Events => Queue.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Queue.Enqueue(eventId.Id);
    }
}
