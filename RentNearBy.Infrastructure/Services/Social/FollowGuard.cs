using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using StackExchange.Redis;
using static RentNearBy.Core.Models.Social.FollowGuardRules;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// A follow attempt that passed the Redis front gate. Reserved = a slot of the week was taken (token in fg:{u}:s);
// Full = Redis says the week or day is full, so only a follow-back (or a Redis over-count) can pass the DB check;
// ViaRedis = false when Redis was down or absent (the DB check alone decides).
public sealed record FollowGuardTicket(Guid Me, Guid Target, string Token, bool Reserved, bool Full, bool ViaRedis)
{
    public bool MustBeExempt => Full;
}

// Discover follow guard (DESIGN 4.4-4.7): one atomic Lua step per call, before any DB access. The DB copy on UserProfiles is the
// source of truth; Redis fails open to it through a circuit breaker, with an in-process burst window while Redis is down.
public sealed class FollowGuard(IConnectionMultiplexer? redis, IMemoryCache memory, ILogger<FollowGuard> logger, TimeProvider? clock = null)
{
    public const long BreakMs = 30_000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _openUntilMs;

    // KEYS = h, s, a, x, c, b, u in every script (FollowGuardKeys.All).
    private static readonly string Escalate = $$"""
        local function escalate(now)
          local level = tonumber(redis.call('HGET', KEYS[6], 'level') or '0')
          local levelAt = tonumber(redis.call('HGET', KEYS[6], 'levelAt') or '0')
          if now - levelAt > {{LevelForgetMs}} then level = 0 end
          level = math.min(level + 1, {{LevelMax}})
          local ladder = { {{Level1Ms}}, {{Level2Ms}}, {{Level3Ms}} }
          local untilMs = now + ladder[level]
          redis.call('HSET', KEYS[6], 'until', untilMs, 'level', level, 'levelAt', now)
          redis.call('PEXPIRE', KEYS[6], untilMs - now + {{LevelForgetMs}})
          redis.call('DEL', KEYS[3], KEYS[4], KEYS[5])
          return {{{FollowGuardCodes.BlockedNew}}, untilMs, level}
        end
        local now = tonumber(ARGV[1])
        if redis.call('EXISTS', KEYS[1]) == 0 then return {{{FollowGuardCodes.Hydrate}}, 0, 0} end
        if redis.call('EXISTS', KEYS[6]) == 0 then
          redis.call('DEL', KEYS[1])
          return {{{FollowGuardCodes.Hydrate}}, 0, 0}
        end
        local blockedUntil = tonumber(redis.call('HGET', KEYS[6], 'until') or '0')
        if blockedUntil > now then
          return {{{FollowGuardCodes.Blocked}}, blockedUntil, tonumber(redis.call('HGET', KEYS[6], 'level') or '0')}
        end
        """;

    // Raw strings drop the last newline: every part ends with one, so no 'end' runs into the next statement.
    private static string Lua(params string[] parts) => string.Concat(parts.Select(p => p.EndsWith('\n') ? p : p + "\n"));

    // ARGV = now ms, token, targetId (guid N).
    internal static readonly string TryScript = Lua(Escalate, $$"""
        redis.call('PEXPIRE', KEYS[1], {{HydratedTtlMs}})
        if redis.call('PTTL', KEYS[6]) < {{HydratedTtlMs}} then redis.call('PEXPIRE', KEYS[6], {{HydratedTtlMs}}) end
        redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', now - {{BurstWindowMs}})
        redis.call('ZADD', KEYS[3], now, ARGV[2])
        redis.call('PEXPIRE', KEYS[3], {{BurstWindowMs}})
        if redis.call('ZCARD', KEYS[3]) > {{BurstMax}} then return escalate(now) end
        local un = redis.call('ZSCORE', KEYS[7], ARGV[3])
        if un and tonumber(un) > now - {{ChurnWindowMs}} then
          redis.call('ZREMRANGEBYSCORE', KEYS[5], '-inf', now - {{ChurnWindowMs}})
          redis.call('ZADD', KEYS[5], now, ARGV[2])
          redis.call('PEXPIRE', KEYS[5], {{ChurnWindowMs}})
          if redis.call('ZCARD', KEYS[5]) >= {{ChurnMax}} then return escalate(now) end
        end
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now - {{WeekMs}})
        local week = redis.call('ZCARD', KEYS[2])
        local day = redis.call('ZCOUNT', KEYS[2], now - {{DayMs}} + 1, '+inf')
        if week < {{WeekMax}} and ({{DayMax}} == 0 or day < {{DayMax}}) then
          redis.call('ZADD', KEYS[2], now, ARGV[2])
          redis.call('PEXPIRE', KEYS[2], {{WeekMs}})
          return {{{FollowGuardCodes.Reserved}}, 0, 0}
        end
        return {{{FollowGuardCodes.Full}}, 0, 0}
        """);

    // ARGV = now ms, token.
    internal static readonly string WastedScript = Lua(Escalate, $$"""
        redis.call('ZREMRANGEBYSCORE', KEYS[4], '-inf', now - {{HammerWindowMs}})
        redis.call('ZADD', KEYS[4], now, ARGV[2])
        redis.call('PEXPIRE', KEYS[4], {{HammerWindowMs}})
        if redis.call('ZCARD', KEYS[4]) >= {{HammerMax}} then return escalate(now) end
        return {{{FollowGuardCodes.Full}}, 0, 0}
        """);

    // ARGV = token. No guards.
    internal const string ReleaseScript = """
        return redis.call('ZREM', KEYS[2], ARGV[1])
        """;

    // ARGV = now ms, targetId. No guards; never refuses, never escalates.
    internal static readonly string UnfollowedScript = $$"""
        local now = tonumber(ARGV[1])
        redis.call('ZADD', KEYS[7], now, ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[7], '-inf', now - {{ChurnWindowMs}})
        redis.call('ZREMRANGEBYRANK', KEYS[7], 0, -{{UnfollowedKeep + 1}})
        redis.call('PEXPIRE', KEYS[7], {{ChurnWindowMs}})
        return 1
        """;

    // ARGV = now ms, untilMs, level, levelAt ms, t1..tn. Replaces s (never merges); keeps a Redis-only block (max of until/level).
    // b is always written and outlives h, so a missing b with h present means an eviction (TRY/WASTED then re-hydrate).
    internal static readonly string HydrateScript = $$"""
        if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
        local now = tonumber(ARGV[1])
        redis.call('DEL', KEYS[2])
        for i = 5, #ARGV do
          local t = tonumber(ARGV[i])
          if t > now - {{WeekMs}} then redis.call('ZADD', KEYS[2], t, 'db:' .. ARGV[i] .. ':' .. (i - 4)) end
        end
        if redis.call('EXISTS', KEYS[2]) == 1 then redis.call('PEXPIRE', KEYS[2], {{WeekMs}}) end
        local untilMs = math.max(tonumber(redis.call('HGET', KEYS[6], 'until') or '0'), tonumber(ARGV[2]))
        local level = math.max(tonumber(redis.call('HGET', KEYS[6], 'level') or '0'), tonumber(ARGV[3]))
        local levelAt = math.max(tonumber(redis.call('HGET', KEYS[6], 'levelAt') or '0'), tonumber(ARGV[4]))
        redis.call('HSET', KEYS[6], 'until', untilMs, 'level', level, 'levelAt', levelAt)
        redis.call('PEXPIRE', KEYS[6], math.max(math.max(untilMs, levelAt) + {{LevelForgetMs}} - now, {{HydratedTtlMs}}))
        redis.call('SET', KEYS[1], '1', 'PX', {{HydratedTtlMs}})
        return 1
        """;

    private const string StateSql = """
        SELECT p."FollowSendTimes" AS "Times", p."FollowBlockUntil" AS "BlockUntil", p."FollowBlockLevel" AS "Level",
               p."FollowBlockLevelAt" AS "LevelAt"
        FROM "UserProfiles" p WHERE p."UserId" = @me
        """;

    // The level never drops: the higher of the asked level and the stored level + 1 (inside the forget window); until follows it.
    internal static readonly string PersistBlockSql = $$"""
        UPDATE "UserProfiles" p
        SET "FollowBlockLevel" = n."Level",
            "FollowBlockUntil" = GREATEST(@until, @at + interval '1 millisecond'
                * CASE n."Level" WHEN 1 THEN {{Level1Ms}} WHEN 2 THEN {{Level2Ms}} ELSE {{Level3Ms}} END),
            "FollowBlockLevelAt" = @at
        FROM (
            SELECT GREATEST(@level, CASE WHEN q."FollowBlockLevelAt" >= @at - interval '1 millisecond' * {{LevelForgetMs}}
                                         THEN LEAST(q."FollowBlockLevel" + 1, {{LevelMax}}) ELSE 1 END) AS "Level"
            FROM "UserProfiles" q WHERE q."UserId" = @me
        ) n
        WHERE p."UserId" = @me AND COALESCE(p."FollowBlockUntil", '-infinity') < @until
        """;

    // ------------------------------------------------------------------ TRY

    // Null = refused (429 TryLater): an active block, a new block, or the in-process burst limit while Redis is down.
    public async Task<FollowGuardTicket?> TryAsync(ApplicationDbContext db, Guid me, Guid target, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        if (redis is null || !Enter()) return await FallbackAsync(db, me, target, token, ct);
        FollowGuardAnswer r;
        try
        {
            r = await TryOnceAsync(me, target, token);
            if (r.Code == FollowGuardCodes.Hydrate)
            {
                await HydrateAsync(db, me, ct);
                r = await TryOnceAsync(me, target, token);
            }
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            Broken(ex);
            return await FallbackAsync(db, me, target, token, ct);
        }

        switch (r.Code)
        {
            case FollowGuardCodes.Reserved: return new FollowGuardTicket(me, target, token, true, false, true);
            case FollowGuardCodes.Full: return new FollowGuardTicket(me, target, token, false, true, true);
            case FollowGuardCodes.Blocked: return null;
            case FollowGuardCodes.BlockedNew:
                await PersistBlockAsync(db, me, r, CancellationToken.None);
                return null;
            default:
                // A second HYDRATE (the marker was dropped again in between): the DB check alone decides.
                return new FollowGuardTicket(me, target, token, false, false, true);
        }
    }

    private Task<FollowGuardAnswer> TryOnceAsync(Guid me, Guid target, string token)
        => EvalAnswerAsync(TryScript, me, [NowMs(), token, target.ToString("N")]);

    // ------------------------------------------------------------------ after the DB transaction (once, outside the DB retry)

    public async Task CompleteAsync(ApplicationDbContext db, FollowGuardTicket ticket, FollowGuardOutcome? outcome)
    {
        var steps = FollowGuardSteps.Of(ticket.Reserved, outcome);
        if (!steps.Release && !steps.DropMarker && !steps.Wasted) return;
        if (!ticket.ViaRedis || redis is null || !Enter()) return;
        try
        {
            if (steps.Release) await EvalAsync(ReleaseScript, ticket.Me, [ticket.Token]);
            if (steps.DropMarker)
            {
                await redis.GetDatabase().KeyDeleteAsync(FollowGuardKeys.Hydrated(ticket.Me));
                Healthy();
            }
            FollowGuardAnswer? escalated = null;
            if (steps.Wasted)
            {
                var r = await EvalAnswerAsync(WastedScript, ticket.Me, [NowMs(), ticket.Token]);
                if (r.Code == FollowGuardCodes.Hydrate)
                {
                    await HydrateAsync(db, ticket.Me, CancellationToken.None);
                    r = await EvalAnswerAsync(WastedScript, ticket.Me, [NowMs(), ticket.Token]);
                }
                if (r.Code == FollowGuardCodes.BlockedNew) escalated = r;
            }
            if (escalated is { } block) await PersistBlockAsync(db, ticket.Me, block, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Runs in the handler's finally: it never throws over the follow answer.
            if (IsRedisFailure(ex)) Broken(ex);
            else logger.LogWarning(ex, "follow.guard.complete_failed {UserId}", ticket.Me);
        }
    }

    // After a committed unfollow or cancel that deleted a row (best effort; churn is judged at the re-follow, in TRY).
    public async Task UnfollowedAsync(Guid me, Guid target)
    {
        if (redis is null || !Enter()) return;
        try
        {
            await EvalAsync(UnfollowedScript, me, [NowMs(), target.ToString("N")]);
        }
        catch (Exception ex)
        {
            if (IsRedisFailure(ex)) Broken(ex);
            else logger.LogWarning(ex, "follow.guard.unfollowed_failed {UserId}", me);
        }
    }

    // ------------------------------------------------------------------ DB copy

    private async Task HydrateAsync(ApplicationDbContext db, Guid me, CancellationToken ct)
    {
        var row = (await db.Database.SqlQueryRaw<StateRow>(StateSql, Uuid("me", me)).ToListAsync(ct)).FirstOrDefault();
        var args = new List<RedisValue>
        {
            NowMs(),
            row?.BlockUntil is { } until ? FollowGuardModel.Ms(until) : 0,
            (int)(row?.Level ?? 0),
            row?.LevelAt is { } at ? FollowGuardModel.Ms(at) : 0,
        };
        foreach (var t in row?.Times ?? []) args.Add(FollowGuardModel.Ms(t));
        await EvalAsync(HydrateScript, me, args.ToArray());
    }

    // A failed persist keeps the Redis-only block (HYDRATE never shortens it); the request is refused either way.
    private async Task PersistBlockAsync(ApplicationDbContext db, Guid me, FollowGuardAnswer block, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(PersistBlockSql,
                [Uuid("me", me), At("until", FollowGuardModel.FromMs(block.UntilMs)), Short("level", block.Level), At("at", Now())], ct);
            logger.LogInformation("follow.guard.block {UserId} level {Level}", me, block.Level);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "follow.guard.block_persist_failed {UserId} level {Level}", me, block.Level);
        }
    }

    // ------------------------------------------------------------------ Redis down: in-process burst window, ladder in the DB

    private async Task<FollowGuardTicket?> FallbackAsync(ApplicationDbContext db, Guid me, Guid target, string token, CancellationToken ct)
    {
        var log = memory.GetOrCreate($"fg:burst:{me:N}", e =>
        {
            e.SlidingExpiration = TimeSpan.FromMilliseconds(2 * BurstWindowMs);
            return new SlidingWindowLog();
        })!;
        bool allowed;
        lock (log)
        {
            allowed = log.TryAcquire(NowMs(), BurstWindowMs, BurstMax, 1, token).Allowed;
            if (!allowed) log.Prune(long.MaxValue);
        }
        if (allowed) return new FollowGuardTicket(me, target, token, false, false, false);
        await EscalateInDbAsync(db, me, ct);
        return null;
    }

    private async Task EscalateInDbAsync(ApplicationDbContext db, Guid me, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await BuddyLocks.LockSenderAsync(db, me, ct);
        var row = (await db.Database.SqlQueryRaw<StateRow>(StateSql, Uuid("me", me)).ToListAsync(ct)).FirstOrDefault();
        var now = Now();
        if (row is not null && !(row.BlockUntil is { } until && until > now))
        {
            var (level, blockUntil) = FollowGuardModel.Escalate(row.Level, row.LevelAt, now);
            await db.Database.ExecuteSqlRawAsync(PersistBlockSql,
                [Uuid("me", me), At("until", blockUntil), Short("level", level), At("at", now)], ct);
            logger.LogInformation("follow.guard.block {UserId} level {Level} (no redis)", me, level);
        }
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------ circuit breaker

    // Closed: every call goes to Redis. Open (30 s after a Redis error): no call. After 30 s exactly one caller wins the probe
    // (it pushes the window 30 s on); an answer closes the breaker, a failure keeps it open, a lost probe just waits 30 s more.
    private bool Enter()
    {
        var until = Interlocked.Read(ref _openUntilMs);
        if (until == 0) return true;
        var now = NowMs();
        if (now < until) return false;
        return Interlocked.CompareExchange(ref _openUntilMs, now + BreakMs, until) == until;
    }

    private void Healthy()
    {
        if (Interlocked.Read(ref _openUntilMs) != 0) Interlocked.Exchange(ref _openUntilMs, 0);
    }

    private void Broken(Exception ex)
    {
        Interlocked.Exchange(ref _openUntilMs, NowMs() + BreakMs);
        logger.LogWarning(ex, "follow.guard.redis_down, the DB check decides for {Seconds} s", BreakMs / 1000);
    }

    public bool IsOpen => redis is null || Interlocked.Read(ref _openUntilMs) != 0;

    internal static bool IsRedisFailure(Exception ex) => ex is RedisException or RedisTimeoutException or TimeoutException;

    // ------------------------------------------------------------------ helpers

    private async Task<FollowGuardAnswer> EvalAnswerAsync(string script, Guid me, RedisValue[] args)
    {
        var r = (RedisResult[]?)await EvalAsync(script, me, args);
        if (r is null || r.Length < 3) return FollowGuardAnswer.Hydrate;
        return new FollowGuardAnswer((int)(long)r[0], (long)r[1], (int)(long)r[2]);
    }

    // A call that answered closes the breaker (a later DB error can never leave the half-open probe taken).
    private async Task<RedisResult> EvalAsync(string script, Guid me, RedisValue[] args)
    {
        var result = await redis!.GetDatabase().ScriptEvaluateAsync(script, FollowGuardKeys.All(me).Select(k => (RedisKey)k).ToArray(), args);
        Healthy();
        return result;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private long NowMs() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private static Npgsql.NpgsqlParameter Short(string name, int value) => new(name, NpgsqlTypes.NpgsqlDbType.Smallint) { Value = (short)value };

    private sealed class StateRow
    {
        public DateTime[] Times { get; set; } = [];
        public DateTime? BlockUntil { get; set; }
        public short Level { get; set; }
        public DateTime? LevelAt { get; set; }
    }
}
