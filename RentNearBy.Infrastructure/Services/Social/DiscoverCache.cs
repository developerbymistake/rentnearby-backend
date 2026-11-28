using System.Globalization;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Models.Social;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services.Social;

// One snapshot row: the zset member and score, and its decoded hash value (null = missing or unreadable, the row is skipped).
public sealed record DiscoverSnapshotRow(Guid UserId, double Score, DiscoverRowCode? Code)
{
    // Points are the integer part of the unique score (DiscoverRules.ZScore), so a cursor never needs the code.
    public int Points => (int)Math.Floor(Score);
}

public sealed record DiscoverSlice(long Stamp, string? Trip, IReadOnlyList<DiscoverSnapshotRow> Rows);

// The Redis side of Discover (DESIGN 3.1-3.4): snapshot read, stampede lock, write, refresh and invalidation. Best effort: with no
// Redis, or while the breaker is open after an error (30 s), every read is a miss and every write a no-op.
public sealed class DiscoverCache(IConnectionMultiplexer? redis, TimeProvider clock, ILogger<DiscoverCache> logger)
{
    public const string TripField = "t";
    public static readonly TimeSpan BreakerOpen = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(50);
    public const int PollTimes = 20;

    // KEYS z, r, s. ARGV max ("+inf" or "(score"), take, withTrip. Nil = no snapshot. Else {stamp, trip, id, score, code, ...}.
    // The stamp value is "stamp|rowCount": a stamp whose rows say > 0 while :z or :r is gone (partial eviction) is a miss.
    internal const string ReadScript = """
        local v = redis.call('GET', KEYS[3])
        if not v then return false end
        local bar = string.find(v, '|', 1, true)
        if not bar then return false end
        local n = tonumber(string.sub(v, bar + 1))
        if not n then return false end
        if n > 0 and (redis.call('EXISTS', KEYS[1]) == 0 or redis.call('EXISTS', KEYS[2]) == 0) then return false end
        local s = string.sub(v, 1, bar - 1)
        local t = ''
        if ARGV[3] == '1' then t = redis.call('HGET', KEYS[2], 't') or '' end
        local z = redis.call('ZREVRANGEBYSCORE', KEYS[1], ARGV[1], '-inf', 'WITHSCORES', 'LIMIT', 0, tonumber(ARGV[2]))
        local out = {s, t}
        if #z > 0 then
          local ids = {}
          for i = 1, #z, 2 do ids[#ids + 1] = z[i] end
          local vals = redis.call('HMGET', KEYS[2], unpack(ids))
          for i = 1, #ids do
            out[#out + 1] = ids[i]
            out[#out + 1] = z[i * 2]
            out[#out + 1] = vals[i] or ''
          end
        end
        return out
        """;

    // Compare-and-delete: a build slower than the lock TTL never deletes another instance's lock.
    internal const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private long _openUntilTicks;

    public bool Available => redis is { IsConnected: true } && clock.GetUtcNow().UtcTicks >= Interlocked.Read(ref _openUntilTicks);

    private IDatabase Db => redis!.GetDatabase();

    private void Fail(Exception ex, string what)
    {
        Interlocked.Exchange(ref _openUntilTicks, clock.GetUtcNow().Add(BreakerOpen).UtcTicks);
        logger.LogWarning(ex, "discover.cache.{What}_failed", what);
    }

    public static string StampValue(long stamp, int rows)
        => string.Create(CultureInfo.InvariantCulture, $"{stamp}|{rows}");

    // "+inf" for the first page, else the exclusive bound "(score" (round-trip text, so the exact double reaches Redis).
    public static string MaxArg(double? below) => below is { } b ? "(" + b.ToString("R", CultureInfo.InvariantCulture) : "+inf";

    // null = no snapshot (or Redis unavailable): the caller builds.
    public async Task<DiscoverSlice?> ReadAsync(Guid me, double? below, int take, bool withTrip)
    {
        if (!Available) return null;
        try
        {
            var result = await Db.ScriptEvaluateAsync(ReadScript,
                [DiscoverKeys.Scores(me), DiscoverKeys.Rows(me), DiscoverKeys.Stamp(me)],
                [MaxArg(below), take, withTrip ? "1" : "0"]);
            if (result.IsNull) return null;
            var parts = (RedisResult[])result!;
            if (parts.Length < 2 || !long.TryParse((string?)parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamp)) return null;
            var trip = (string?)parts[1];
            var rows = new List<DiscoverSnapshotRow>((parts.Length - 2) / 3);
            for (var i = 2; i + 2 < parts.Length; i += 3)
            {
                if (!Guid.TryParseExact((string?)parts[i], "N", out var id)) continue;
                if (!double.TryParse((string?)parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var score)) continue;
                rows.Add(new DiscoverSnapshotRow(id, score, DiscoverRowCode.TryDecode((string?)parts[i + 2], out var code) ? code : null));
            }
            return new DiscoverSlice(stamp, string.IsNullOrEmpty(trip) ? null : trip, rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex, "read");
            return null;
        }
    }

    // A token when this caller won the build lock, null when another build holds it (or Redis is unavailable).
    public async Task<string?> TryLockAsync(Guid me)
    {
        if (!Available) return null;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            return await Db.StringSetAsync(DiscoverKeys.Lock(me), token, TimeSpan.FromMilliseconds(DiscoverKeys.LockTtlMs), When.NotExists) ? token : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex, "lock");
            return null;
        }
    }

    public async Task ReleaseAsync(Guid me, string token)
    {
        if (redis is null) return;
        try
        {
            await Db.ScriptEvaluateAsync(ReleaseScript, [DiscoverKeys.Lock(me)], [token]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex, "release");
        }
    }

    // A loser of the lock waits up to 1 s for the winner's stamp. False = build without writing.
    public async Task<bool> WaitForSnapshotAsync(Guid me, CancellationToken ct)
    {
        for (var i = 0; i < PollTimes && Available; i++)
        {
            await Task.Delay(PollEvery, ct); // batch-loop: bounded lock wait, at most PollTimes
            try
            {
                if (await Db.KeyExistsAsync(DiscoverKeys.Stamp(me))) return true; // batch-loop
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(ex, "poll");
                return false;
            }
        }
        return false;
    }

    // One MULTI: DEL first (ZADD / HSET would merge into stale members), then the rows, the trip header field and the stamp, all
    // with the 6 h TTL counted from the build (never refreshed on reads).
    public async Task WriteAsync(Guid me, long stamp, IReadOnlyList<DiscoverSnapshotRow> rows, string? trip)
    {
        if (!Available) return;
        var z = DiscoverKeys.Scores(me);
        var r = DiscoverKeys.Rows(me);
        var s = DiscoverKeys.Stamp(me);
        var ttl = TimeSpan.FromMilliseconds(DiscoverKeys.SnapshotTtlMs);
        var hash = rows.Where(x => x.Code != null).Select(x => new HashEntry(DiscoverKeys.Member(x.UserId), x.Code!.Encode())).ToList();
        if (trip != null) hash.Add(new HashEntry(TripField, trip));
        try
        {
            var tx = Db.CreateTransaction();
            _ = tx.KeyDeleteAsync([z, r, s]);
            if (rows.Count > 0) _ = tx.SortedSetAddAsync(z, rows.Select(x => new SortedSetEntry(DiscoverKeys.Member(x.UserId), x.Score)).ToArray());
            if (hash.Count > 0) _ = tx.HashSetAsync(r, hash.ToArray());
            _ = tx.StringSetAsync(s, StampValue(stamp, rows.Count), ttl);
            if (rows.Count > 0) _ = tx.KeyExpireAsync(z, ttl);
            if (hash.Count > 0) _ = tx.KeyExpireAsync(r, ttl);
            await tx.ExecuteAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex, "write");
        }
    }

    // Pull to refresh: drops the snapshot at most once per 30 s per user.
    public async Task RefreshAsync(Guid me)
    {
        if (!Available) return;
        try
        {
            if (await Db.StringSetAsync(DiscoverKeys.Refresh(me), 1, TimeSpan.FromMilliseconds(DiscoverKeys.RefreshGapMs), When.NotExists))
                await Db.KeyDeleteAsync(DiscoverKeys.Snapshot(me).Select(k => (RedisKey)k).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex, "refresh");
        }
    }

    // DESIGN 3.2: the user's own trips, mates, area, interests or blocks changed. Fire and forget, never throws, never waits.
    public void Invalidate(params Guid[] users)
    {
        if (!Available) return;
        try
        {
            foreach (var u in users.Distinct())
                Db.KeyDelete(DiscoverKeys.Snapshot(u).Select(k => (RedisKey)k).ToArray(), CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            Fail(ex, "invalidate");
        }
    }
}
