using System.Runtime.CompilerServices;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;
using StackExchange.Redis;
using static RentNearBy.Core.Models.Social.FollowGuardRules;

namespace RentNearBy.Tests.Social.Discover;

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_TEST_URL")))
            Skip = "REDIS_TEST_URL is not set (the follow guard Lua parity tests need a real Redis).";
    }
}

// DESIGN 10 / L7: the same cases as FollowGuardModelTests, run step by step against the real Lua scripts and the C# model, every
// answer compared. Skipped without REDIS_TEST_URL; REQUIRED before go-live. Each test uses fresh user keys and deletes them.
public class FollowGuardRedisParityTests
{
    private const long Minute = 60_000;

    private sealed class Pair : IAsyncDisposable
    {
        private static readonly Lazy<ConnectionMultiplexer> Mux =
            new(() => ConnectionMultiplexer.Connect(Environment.GetEnvironmentVariable("REDIS_TEST_URL")!));

        public readonly Guid User = Guid.NewGuid();
        public readonly FollowGuardModel Model = new();
        private readonly IDatabase _db = Mux.Value.GetDatabase();
        private int _seq;
        public long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private RedisKey[] Keys => [.. FollowGuardKeys.All(User).Select(k => (RedisKey)k)];

        private async Task<RedisResult> EvalAsync(string script, params RedisValue[] args) => await _db.ScriptEvaluateAsync(script, Keys, args);

        private static FollowGuardAnswer Answer(RedisResult r)
        {
            var a = (RedisResult[])r!;
            return new FollowGuardAnswer((int)(long)a[0], (long)a[1], (int)(long)a[2]);
        }

        public async Task HydrateAsync(long untilMs = 0, int level = 0, long levelAtMs = 0, params long[] times)
        {
            var args = new List<RedisValue> { Now, untilMs, level, levelAtMs };
            args.AddRange(times.Select(t => (RedisValue)t));
            var redis = (int)(long)await EvalAsync(FollowGuard.HydrateScript, [.. args]);
            Assert.Equal(Model.Hydrate(Now, untilMs, level, levelAtMs, times), redis);
        }

        public async Task<FollowGuardAnswer> TryAsync(Guid? target = null, string? token = null)
        {
            token ??= $"t{++_seq}";
            var him = target ?? Guid.NewGuid();
            var redis = Answer(await EvalAsync(FollowGuard.TryScript, Now, token, him.ToString("N")));
            Assert.Equal(Model.Try(Now, token, him), redis);
            return redis;
        }

        public async Task<FollowGuardAnswer> WastedAsync()
        {
            var token = $"w{++_seq}";
            var redis = Answer(await EvalAsync(FollowGuard.WastedScript, Now, token));
            Assert.Equal(Model.Wasted(Now, token), redis);
            return redis;
        }

        public async Task ReleaseAsync(string token)
        {
            await EvalAsync(FollowGuard.ReleaseScript, token);
            Model.Release(token);
        }

        public async Task UnfollowedAsync(Guid target)
        {
            await EvalAsync(FollowGuard.UnfollowedScript, Now, target.ToString("N"));
            Model.Unfollowed(Now, target);
        }

        public async Task DropMarkerAsync()
        {
            await _db.KeyDeleteAsync(FollowGuardKeys.Hydrated(User));
            Model.DropMarker();
        }

        // A partial eviction of fg:{u}:b only: the scripts drop h and ask for a HYDRATE, so the model just loses its marker.
        public async Task EvictBlockAsync()
        {
            await _db.KeyDeleteAsync(FollowGuardKeys.Block(User));
            Model.DropMarker();
        }

        public async Task FlushAsync()
        {
            await _db.KeyDeleteAsync(Keys);
            Model.Flush();
        }

        public async Task<long> SendsAsync()
        {
            var n = await _db.SortedSetLengthAsync(FollowGuardKeys.Sends(User));
            Assert.Equal(Model.Sends, (int)n);
            return n;
        }

        public async ValueTask DisposeAsync() => await _db.KeyDeleteAsync(Keys);
    }

    [RedisFact]
    public async Task Every_script_answers_hydrate_first()
    {
        await using var p = new Pair();
        Assert.Equal(FollowGuardCodes.Hydrate, (await p.TryAsync()).Code);
        Assert.Equal(FollowGuardCodes.Hydrate, (await p.WastedAsync()).Code);
        await p.HydrateAsync();
        await p.HydrateAsync();
        Assert.Equal(FollowGuardCodes.Reserved, (await p.TryAsync()).Code);
    }

    [RedisFact]
    public async Task Week_day_and_slot_release()
    {
        await using var p = new Pair();
        await p.HydrateAsync();
        for (var i = 0; i < DayMax; i++)
        {
            Assert.Equal(FollowGuardCodes.Reserved, (await p.TryAsync()).Code);
            p.Now += 2 * Minute;
        }
        Assert.Equal(FollowGuardCodes.Full, (await p.TryAsync()).Code);
        p.Now += DayMs;
        for (var i = DayMax; i < WeekMax; i++)
        {
            Assert.Equal(FollowGuardCodes.Reserved, (await p.TryAsync(token: $"keep{i}")).Code);
            p.Now += 2 * Minute;
        }
        Assert.Equal(FollowGuardCodes.Full, (await p.TryAsync()).Code);
        await p.ReleaseAsync($"keep{WeekMax - 1}");
        await p.ReleaseAsync("unknown");
        Assert.Equal(WeekMax - 1, await p.SendsAsync());
        Assert.Equal(FollowGuardCodes.Reserved, (await p.TryAsync()).Code);
    }

    [RedisFact]
    public async Task Burst_ladder_and_no_escalation_during_a_block()
    {
        await using var p = new Pair();
        await p.HydrateAsync();
        for (var block = 1; block <= 4; block++)
        {
            FollowGuardAnswer last = default;
            for (var i = 0; i <= BurstMax; i++) last = await p.TryAsync();
            Assert.Equal(FollowGuardCodes.BlockedNew, last.Code);
            Assert.Equal(Math.Min(block, LevelMax), last.Level);
            Assert.Equal(FollowGuardCodes.Blocked, (await p.TryAsync()).Code);
            Assert.Equal(FollowGuardCodes.Blocked, (await p.WastedAsync()).Code);
            p.Now = last.UntilMs + 1;
        }
    }

    [RedisFact]
    public async Task Hammer_and_churn()
    {
        await using var p = new Pair();
        await p.HydrateAsync();
        for (var i = 1; i < HammerMax; i++) Assert.Equal(FollowGuardCodes.Full, (await p.WastedAsync()).Code);
        var hammer = await p.WastedAsync();
        Assert.Equal(FollowGuardCodes.BlockedNew, hammer.Code);
        p.Now = hammer.UntilMs + 1;
        for (var i = 1; i <= ChurnMax; i++)
        {
            var x = Guid.NewGuid();
            await p.TryAsync(x);
            await p.UnfollowedAsync(x);
            p.Now += 2 * Minute;
            var again = await p.TryAsync(x);
            if (i == ChurnMax) Assert.Equal(FollowGuardCodes.BlockedNew, again.Code);
            else Assert.NotEqual(FollowGuardCodes.BlockedNew, again.Code);
            p.Now += 2 * Minute;
        }
        Assert.Equal(2, p.Model.Level);
    }

    [RedisFact]
    public async Task A_flush_keeps_the_db_ladder_and_hydrate_replaces_the_sends()
    {
        await using var p = new Pair();
        await p.HydrateAsync(0, 2, p.Now - DayMs);
        for (var i = 0; i < 2; i++) await p.TryAsync();
        await p.DropMarkerAsync();
        await p.HydrateAsync(0, 2, p.Now - DayMs, p.Now - Minute, p.Now - 2 * Minute);
        Assert.Equal(2, await p.SendsAsync());
        await p.FlushAsync();
        await p.HydrateAsync(0, 2, p.Now - DayMs);
        FollowGuardAnswer last = default;
        for (var i = 0; i <= BurstMax; i++) last = await p.TryAsync();
        Assert.Equal((FollowGuardCodes.BlockedNew, 3), (last.Code, last.Level));
    }

    [RedisFact]
    public async Task An_evicted_block_hash_rehydrates_and_keeps_the_ladder()
    {
        await using var p = new Pair();
        await p.HydrateAsync(0, 2, p.Now - DayMs);
        await p.EvictBlockAsync();
        Assert.Equal(FollowGuardCodes.Hydrate, (await p.TryAsync()).Code);
        await p.HydrateAsync(0, 2, p.Now - DayMs);
        FollowGuardAnswer last = default;
        for (var i = 0; i <= BurstMax; i++) last = await p.TryAsync();
        Assert.Equal((FollowGuardCodes.BlockedNew, 3), (last.Code, last.Level));
    }
}
