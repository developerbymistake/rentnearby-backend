using System.Collections.Concurrent;

namespace RentNearBy.Infrastructure.Services;

public static class WorkerNames
{
    public const string TripTimer = "trip-timer", OutboxRelay = "outbox-relay", PushDelivery = "push-delivery";
}

// Last-alive time of each background worker, read by /health/ready. Singleton; Beat is lock-free.
public sealed class WorkerHeartbeats
{
    private readonly ConcurrentDictionary<string, long> _lastBeatTicks = new();
    private readonly TimeProvider _clock;
    private readonly long _startedTicks;

    public WorkerHeartbeats() : this(TimeProvider.System) { }

    public WorkerHeartbeats(TimeProvider clock)
    {
        _clock = clock;
        _startedTicks = clock.GetUtcNow().UtcTicks;
    }

    public TimeSpan Uptime => TimeSpan.FromTicks(_clock.GetUtcNow().UtcTicks - _startedTicks);

    public void Beat(string name) => _lastBeatTicks[name] = _clock.GetUtcNow().UtcTicks;

    // Null = the worker never beat since this process started.
    public TimeSpan? AgeOf(string name)
        => _lastBeatTicks.TryGetValue(name, out var ticks)
            ? TimeSpan.FromTicks(Math.Max(0, _clock.GetUtcNow().UtcTicks - ticks))
            : null;
}
