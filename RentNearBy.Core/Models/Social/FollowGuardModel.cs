namespace RentNearBy.Core.Models.Social;

// Answer of the TRY / WASTED scripts: {code, untilMs, level} (FollowGuardCodes).
public readonly record struct FollowGuardAnswer(int Code, long UntilMs, int Level)
{
    public static readonly FollowGuardAnswer Reserved = new(FollowGuardCodes.Reserved, 0, 0);
    public static readonly FollowGuardAnswer Full = new(FollowGuardCodes.Full, 0, 0);
    public static readonly FollowGuardAnswer Hydrate = new(FollowGuardCodes.Hydrate, 0, 0);
}

// What one follow write did. The retried DB lambda only returns it; the Redis side acts on it once, after PgRetry (DESIGN 4.5).
public sealed record FollowGuardOutcome(bool Counted, bool HealMarker, bool Wasted)
{
    public static readonly FollowGuardOutcome Nothing = new(false, false, false);
}

// The Redis steps of one finished follow attempt. A missing outcome (exception, rollback) only releases.
public readonly record struct FollowGuardSteps(bool Release, bool DropMarker, bool Wasted)
{
    public static FollowGuardSteps Of(bool reserved, FollowGuardOutcome? outcome)
        => new(reserved && outcome?.Counted != true, outcome?.HealMarker == true, outcome?.Wasted == true);
}

public enum FollowWriteKind { NotFound, Existing, Blocked, FollowingLimit, Full, Insert }

public sealed record FollowWriteDecision(FollowWriteKind Kind, FollowGuardOutcome Guard)
{
    public bool Counted => Guard.Counted;
}

// The exact DB check of FollowService.FollowAsync, inside the transaction under the sender lock (DESIGN 4.5).
public static class FollowWriteRules
{
    public static FollowWriteDecision Decide(
        bool found, bool existing, DateTime? blockUntil, bool atFollowingLimit, bool heFollowsMe,
        IEnumerable<DateTime>? sendTimes, bool mustBeExempt, DateTime nowUtc)
    {
        if (!found) return new(FollowWriteKind.NotFound, new(false, false, true));
        if (existing) return new(FollowWriteKind.Existing, new(false, false, true));
        if (blockUntil is { } until && until > nowUtc) return new(FollowWriteKind.Blocked, new(false, true, true));
        if (atFollowingLimit) return new(FollowWriteKind.FollowingLimit, new(false, false, true));
        if (heFollowsMe) return new(FollowWriteKind.Insert, FollowGuardOutcome.Nothing);
        // Redis said RESERVED but the DB is full: Redis under-counted, drop the marker. Redis said FULL but the DB has room:
        // a phantom over-count, take the slot and drop the marker; that is not a wasted attempt.
        if (FollowGuardRules.IsFull(sendTimes, nowUtc)) return new(FollowWriteKind.Full, new(false, !mustBeExempt, true));
        return new(FollowWriteKind.Insert, new(true, mustBeExempt, false));
    }
}

// C# reference model of the Lua scripts in FollowGuard (one user's keys h, s, a, x, c, b, u), like SlidingWindowLog mirrors
// the rate-limit script. Every call takes the model lock, as one Lua call is atomic. TTLs that only clean up are not modelled;
// the marker TTL is.
public sealed class FollowGuardModel
{
    private readonly object _gate = new();
    private long _markerUntil = long.MinValue;
    private readonly List<(long At, string Member)> _sends = [];
    private readonly List<long> _attempts = [];
    private readonly List<long> _wasted = [];
    private readonly List<long> _churn = [];
    private readonly Dictionary<Guid, long> _unfollowed = [];
    private long _blockUntil;
    private int _level;
    private long _levelAt;
    private bool _hasBlock;

    public int Sends { get { lock (_gate) return _sends.Count; } }
    public int Level { get { lock (_gate) return _level; } }
    public long BlockUntil { get { lock (_gate) return _blockUntil; } }

    public bool Hydrated(long now) { lock (_gate) return _markerUntil > now; }

    // TRY (ARGV = now, token, targetId).
    public FollowGuardAnswer Try(long now, string token, Guid target)
    {
        lock (_gate)
        {
            if (Guards(now) is { } stop) return stop;
            _markerUntil = now + FollowGuardRules.HydratedTtlMs;
            _attempts.RemoveAll(t => t <= now - FollowGuardRules.BurstWindowMs);
            _attempts.Add(now);
            if (_attempts.Count > FollowGuardRules.BurstMax) return Escalate(now);
            if (_unfollowed.TryGetValue(target, out var un) && un > now - FollowGuardRules.ChurnWindowMs)
            {
                _churn.RemoveAll(t => t <= now - FollowGuardRules.ChurnWindowMs);
                _churn.Add(now);
                if (_churn.Count >= FollowGuardRules.ChurnMax) return Escalate(now);
            }
            _sends.RemoveAll(s => s.At <= now - FollowGuardRules.WeekMs);
            var week = _sends.Count;
            var day = _sends.Count(s => s.At > now - FollowGuardRules.DayMs);
            if (week < FollowGuardRules.WeekMax && (FollowGuardRules.DayMax == 0 || day < FollowGuardRules.DayMax))
            {
                _sends.RemoveAll(s => s.Member == token);
                _sends.Add((now, token));
                return FollowGuardAnswer.Reserved;
            }
            return FollowGuardAnswer.Full;
        }
    }

    // WASTED (ARGV = now, token): an attempt that reached the DB and inserted nothing.
    public FollowGuardAnswer Wasted(long now, string token)
    {
        lock (_gate)
        {
            if (Guards(now) is { } stop) return stop;
            _wasted.RemoveAll(t => t <= now - FollowGuardRules.HammerWindowMs);
            _wasted.Add(now);
            if (_wasted.Count >= FollowGuardRules.HammerMax) return Escalate(now);
            return FollowGuardAnswer.Full;
        }
    }

    // RELEASE (ARGV = token): no guards, a no-op for an unknown token.
    public void Release(string token)
    {
        lock (_gate) _sends.RemoveAll(s => s.Member == token);
    }

    // UNFOLLOWED (ARGV = now, targetId): never refuses, never escalates.
    public void Unfollowed(long now, Guid target)
    {
        lock (_gate)
        {
            _unfollowed[target] = now;
            foreach (var old in _unfollowed.Where(p => p.Value <= now - FollowGuardRules.ChurnWindowMs).Select(p => p.Key).ToList())
                _unfollowed.Remove(old);
            foreach (var old in _unfollowed.OrderByDescending(p => p.Value).Skip(FollowGuardRules.UnfollowedKeep).Select(p => p.Key).ToList())
                _unfollowed.Remove(old);
        }
    }

    // HYDRATE (ARGV = now, untilMs, level, levelAt, t1..tn): 0 when another call already hydrated.
    public int Hydrate(long now, long untilMs, int level, long levelAtMs, IEnumerable<long> times)
    {
        lock (_gate)
        {
            if (_markerUntil > now) return 0;
            _sends.Clear();
            var i = 0;
            foreach (var t in times)
            {
                i++;
                if (t > now - FollowGuardRules.WeekMs) _sends.Add((t, $"db:{t}:{i}"));
            }
            _sends.Sort((a, b) => a.At.CompareTo(b.At));
            var newUntil = Math.Max(_hasBlock ? _blockUntil : 0, untilMs);
            var newLevel = Math.Max(_hasBlock ? _level : 0, level);
            var newLevelAt = Math.Max(_hasBlock ? _levelAt : 0, levelAtMs);
            if (newUntil > 0 || newLevel > 0)
            {
                (_blockUntil, _level, _levelAt, _hasBlock) = (newUntil, newLevel, newLevelAt, true);
            }
            _markerUntil = now + FollowGuardRules.HydratedTtlMs;
            return 1;
        }
    }

    // DEL fg:{u}:h (the heal paths).
    public void DropMarker()
    {
        lock (_gate) _markerUntil = long.MinValue;
    }

    // A Redis flush: every key of the user is gone.
    public void Flush()
    {
        lock (_gate)
        {
            _markerUntil = long.MinValue;
            _sends.Clear();
            _attempts.Clear();
            _wasted.Clear();
            _churn.Clear();
            _unfollowed.Clear();
            (_blockUntil, _level, _levelAt, _hasBlock) = (0, 0, 0, false);
        }
    }

    // The two guards every TRY / WASTED script starts with: hydrate first, nothing escalates during an active block.
    private FollowGuardAnswer? Guards(long now)
    {
        if (_markerUntil <= now) return FollowGuardAnswer.Hydrate;
        if (_hasBlock && _blockUntil > now) return new FollowGuardAnswer(FollowGuardCodes.Blocked, _blockUntil, _level);
        return null;
    }

    private FollowGuardAnswer Escalate(long now)
    {
        var level = _hasBlock ? _level : 0;
        var levelAt = _hasBlock ? _levelAt : 0;
        if (now - levelAt > FollowGuardRules.LevelForgetMs) level = 0;
        level = Math.Min(level + 1, FollowGuardRules.LevelMax);
        var until = now + FollowGuardRules.LadderMs[level - 1];
        (_blockUntil, _level, _levelAt, _hasBlock) = (until, level, now, true);
        _attempts.Clear();
        _wasted.Clear();
        _churn.Clear();
        return new FollowGuardAnswer(FollowGuardCodes.BlockedNew, until, level);
    }

    // The same ladder on the DB level columns (Redis down: the in-process burst fallback escalates in the DB).
    public static (int Level, DateTime Until) Escalate(int storedLevel, DateTime? levelAt, DateTime nowUtc)
    {
        var level = FollowGuardRules.NextLevel(storedLevel, levelAt, nowUtc);
        return (level, nowUtc + FollowGuardRules.BlockLength(level));
    }

    public static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
