using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.TaxiRequests;

public class TripTimerLoopTests
{
    [Fact]
    public async Task Reader_failure_does_not_escape()
    {
        var reader = new ThrowingReader();
        var logger = new ListLogger<TripTimerService>();
        var service = new TripTimerService(new CountingScopeFactory(), reader, logger);

        await service.TickAsync(CancellationToken.None);

        Assert.Equal(1, reader.Calls);
        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(7004, entry.EventId.Id);
        Assert.IsType<InvalidOperationException>(entry.Exception);

        await service.StartAsync(CancellationToken.None);
        await reader.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(service.ExecuteTask!.IsCompleted);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask.Status);
    }

    [Fact]
    public async Task Failing_pass_does_not_stop_later_passes()
    {
        var ran = new List<string>();
        var logger = new ListLogger<TripTimerLoopTests>();
        var counts = await TripTimerLoop.RunPassesAsync(
        [
            ("one", (_, _) => { ran.Add("one"); return Task.FromResult(2); }),
            ("two", (_, _) => { ran.Add("two"); throw new InvalidOperationException("boom"); }),
            ("three", async (_, _) => { await Task.Yield(); ran.Add("three"); throw new TimeoutException(); }),
            ("four", (_, _) => { ran.Add("four"); return Task.FromResult(5); }),
        ], new CountingScopeFactory(), logger, CancellationToken.None);

        Assert.Equal(["one", "two", "three", "four"], ran);
        Assert.Equal([2, 0, 0, 5], counts);
        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal(7004, e.EventId.Id));
        Assert.Contains("two", errors[0].Message);
        Assert.Contains("three", errors[1].Message);
    }

    [Fact]
    public async Task Each_pass_gets_its_own_scope()
    {
        var factory = new CountingScopeFactory();
        var seen = new List<IServiceProvider>();
        Func<IServiceProvider, CancellationToken, Task<int>> record = (sp, _) => { seen.Add(sp); return Task.FromResult(0); };

        await TripTimerLoop.RunPassesAsync(
            [("a", record), ("b", (_, _) => throw new InvalidOperationException()), ("c", record)],
            factory, new ListLogger<TripTimerLoopTests>(), CancellationToken.None);

        Assert.Equal(3, factory.Scopes.Count);
        Assert.All(factory.Scopes, s => Assert.True(s.Disposed));
        Assert.Equal(2, seen.Distinct().Count());
        Assert.Same(factory.Scopes[0].ServiceProvider, seen[0]);
        Assert.Same(factory.Scopes[2].ServiceProvider, seen[1]);
    }

    [Fact]
    public async Task Cancellation_stops_the_loop()
    {
        using var cts = new CancellationTokenSource();
        var ran = new List<string>();
        var logger = new ListLogger<TripTimerLoopTests>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TripTimerLoop.RunPassesAsync(
        [
            ("one", (_, ct) => { ran.Add("one"); cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(1); }),
            ("two", (_, _) => { ran.Add("two"); return Task.FromResult(1); }),
        ], new CountingScopeFactory(), logger, cts.Token));
        Assert.Equal(["one"], ran);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);

        var reader = new DisabledReader();
        var service = new TripTimerService(new CountingScopeFactory(), reader, new ListLogger<TripTimerService>());
        await service.StartAsync(CancellationToken.None);
        await reader.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task Non_host_cancellation_counts_as_a_pass_failure()
    {
        var ran = new List<string>();
        var logger = new ListLogger<TripTimerLoopTests>();
        await TripTimerLoop.RunPassesAsync(
        [
            ("one", (_, _) => throw new TaskCanceledException("db timeout")),
            ("two", (_, _) => { ran.Add("two"); return Task.FromResult(1); }),
        ], new CountingScopeFactory(), logger, CancellationToken.None);

        Assert.Equal(["two"], ran);
        Assert.Equal(7004, Assert.Single(logger.Entries, e => e.Level == LogLevel.Error).EventId.Id);
    }

    private sealed class ThrowingReader : ITripExecutionFeatureReader
    {
        public int Calls;
        public readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> IsEnabledAsync()
        {
            Interlocked.Increment(ref Calls);
            Called.TrySetResult();
            throw new InvalidOperationException("settings read failed");
        }

        public void Invalidate() { }
    }

    private sealed class DisabledReader : ITripExecutionFeatureReader
    {
        public int Calls;
        public readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> IsEnabledAsync()
        {
            Interlocked.Increment(ref Calls);
            Called.TrySetResult();
            return Task.FromResult(false);
        }

        public void Invalidate() { }
    }

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        public readonly List<FakeScope> Scopes = [];

        public IServiceScope CreateScope()
        {
            var scope = new FakeScope();
            Scopes.Add(scope);
            return scope;
        }
    }

    private sealed class FakeScope : IServiceScope
    {
        public bool Disposed;
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();
        public void Dispose() => Disposed = true;
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public readonly List<LogEntry> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
