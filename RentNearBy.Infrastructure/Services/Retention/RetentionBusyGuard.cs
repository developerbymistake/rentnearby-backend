using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services.Retention;

public enum RetentionLoad { Normal, Busy, Heavy }

// Pure: adaptive batch size and pause from batch timing and the active-session count.
public static class RetentionBusyGuard
{
    public const int MaxPauseMs = 5000;
    public const int HeavyPauseMs = 30_000;
    public const int MaxHeavyPauses = 3;
    public static readonly TimeSpan OutboxBacklogAge = TimeSpan.FromMinutes(5);

    public static RetentionLoad Classify(int activeSessions, RetentionOptions o)
        => activeSessions >= o.HeavyActive ? RetentionLoad.Heavy : activeSessions >= o.BusyActive ? RetentionLoad.Busy : RetentionLoad.Normal;

    public static int NextBatch(int current, long lastBatchMs, RetentionOptions o)
    {
        if (lastBatchMs > o.BusyBatchMs) return Math.Max(o.MinBatchSize, current / 2);
        if (lastBatchMs < o.FastBatchMs) return Math.Min(o.BatchSize, Math.Max(current + 1, current * 2));
        return current;
    }

    public static int NextPause(int currentPauseMs, long lastBatchMs, RetentionOptions o)
    {
        if (lastBatchMs > o.BusyBatchMs) return Math.Min(MaxPauseMs, Math.Max(o.PauseMs, currentPauseMs * 2));
        if (lastBatchMs < o.FastBatchMs) return o.PauseMs;
        return currentPauseMs;
    }

    public static int BatchFor(RetentionLoad load, int current, RetentionOptions o)
        => load == RetentionLoad.Normal ? current : o.MinBatchSize;
}
