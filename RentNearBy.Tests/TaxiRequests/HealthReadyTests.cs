using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using RentNearBy.Api.Handlers;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.TaxiRequests;

public class HealthReadyTests
{
    private static readonly string[] AllWorkers = [WorkerNames.TripTimer, WorkerNames.OutboxRelay, WorkerNames.PushDelivery];

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static (WorkerHeartbeats Beats, FakeTime Clock) Fresh()
    {
        var clock = new FakeTime();
        var beats = new WorkerHeartbeats(clock);
        foreach (var w in AllWorkers) beats.Beat(w);
        return (beats, clock);
    }

    [Fact]
    public async Task Ready_is_200_when_database_and_heartbeats_are_fine()
    {
        var (beats, _) = Fresh();
        Assert.Equal(200, Status(await HealthHandlers.ReadyAsync(_ => Task.CompletedTask, beats, CancellationToken.None)));
    }

    [Fact]
    public async Task Ready_is_503_when_the_database_check_fails()
    {
        var (beats, _) = Fresh();
        Assert.Equal(503, Status(await HealthHandlers.ReadyAsync(_ => throw new InvalidOperationException("down"), beats, CancellationToken.None)));
    }

    [Fact]
    public async Task Ready_is_503_when_the_database_check_hangs_past_the_timeout()
    {
        var (beats, _) = Fresh();
        var result = await HealthHandlers.ReadyAsync(ct => Task.Delay(Timeout.Infinite, ct), beats, CancellationToken.None);
        Assert.Equal(503, Status(result));
    }

    [Theory]
    [InlineData(WorkerNames.TripTimer, 12 * 60)]
    [InlineData(WorkerNames.OutboxRelay, 31)]
    [InlineData(WorkerNames.PushDelivery, 61)]
    public async Task Ready_is_503_when_a_heartbeat_is_stale(string stale, int seconds)
    {
        var (beats, clock) = Fresh();
        clock.Advance(TimeSpan.FromSeconds(seconds));
        foreach (var w in AllWorkers.Where(w => w != stale)) beats.Beat(w);

        Assert.Equal(503, Status(await HealthHandlers.ReadyAsync(_ => Task.CompletedTask, beats, CancellationToken.None)));
    }

    [Fact]
    public async Task Ready_is_503_when_a_worker_never_started_after_two_minutes()
    {
        var clock = new FakeTime();
        var beats = new WorkerHeartbeats(clock);
        beats.Beat(WorkerNames.TripTimer);
        beats.Beat(WorkerNames.OutboxRelay);
        Assert.Equal(200, Status(await HealthHandlers.ReadyAsync(_ => Task.CompletedTask, beats, CancellationToken.None)));

        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        beats.Beat(WorkerNames.TripTimer);
        beats.Beat(WorkerNames.OutboxRelay);
        Assert.Equal(503, Status(await HealthHandlers.ReadyAsync(_ => Task.CompletedTask, beats, CancellationToken.None)));
    }

    [Fact]
    public void Live_does_no_io()
    {
        var method = typeof(HealthHandlers).GetMethod(nameof(HealthHandlers.Live))!;
        Assert.Empty(method.GetParameters());
        Assert.Equal(200, Status(HealthHandlers.Live()));
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
