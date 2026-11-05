namespace RentNearBy.Core.Models;

// Exact sliding-window limit: a call taking p permits is allowed iff (permits taken in (now - window, now]) + p <= max.
// Any window-long interval therefore holds at most max permits (a fixed window allows 2 x max across its boundary).
// A denied call takes nothing; an allowed call's permits can be given back by its token (work failed after the check).
// Not thread-safe: the caller locks. RedisRateLimitService runs the same rule in one Lua script.
public sealed class SlidingWindowLog
{
    private readonly List<(long At, string Member)> _hits = [];

    public int Count => _hits.Count;

    public static string Member(string token, int index) => $"{token}:{index}";

    // RetryAfterMs = how long until the call would fit (0 when allowed; the whole window when permits > max).
    public (bool Allowed, int Remaining, long RetryAfterMs) TryAcquire(long nowMs, long windowMs, int max, int permits, string token)
    {
        permits = Math.Max(1, permits);
        Prune(nowMs - windowMs);
        var used = _hits.Count;
        if (used + permits <= max)
        {
            var at = InsertIndex(nowMs);
            for (var i = 0; i < permits; i++) _hits.Insert(at + i, (nowMs, Member(token, i)));
            return (true, max - used - permits, 0);
        }

        var need = used + permits - max;
        var retry = need <= used ? _hits[need - 1].At + windowMs - nowMs : windowMs;
        return (false, Math.Max(0, max - used), Math.Max(1, retry));
    }

    public int Release(string token)
    {
        var prefix = token + ":";
        return _hits.RemoveAll(h => h.Member.StartsWith(prefix, StringComparison.Ordinal));
    }

    // Entries at or before the cutoff have left the window.
    public void Prune(long cutoffMs)
    {
        var drop = 0;
        while (drop < _hits.Count && _hits[drop].At <= cutoffMs) drop++;
        if (drop > 0) _hits.RemoveRange(0, drop);
    }

    private int InsertIndex(long at)
    {
        var i = _hits.Count;
        while (i > 0 && _hits[i - 1].At > at) i--;
        return i;
    }
}
