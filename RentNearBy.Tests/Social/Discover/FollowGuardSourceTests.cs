using System.Reflection;
using System.Text.RegularExpressions;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Buddy.Foundation;
using static RentNearBy.Core.Models.Social.FollowGuardRules;

namespace RentNearBy.Tests.Social.Discover;

// DESIGN 10.1 source guards of the follow guard (string pins, existing style).
public class FollowGuardSourceTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([BuddySources.Root(), .. parts]));

    private static string Slice(string text, string from, string to)
    {
        var a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, from);
        var b = text.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, to);
        return text[a..b];
    }

    private static string FollowHandler()
        => Slice(Src("RentNearBy.Api", "Handlers", "Social", "FollowHandlers.cs"),
            "public static async Task<IResult> Follow(", "public static Task<IResult> Unfollow(");

    private static string FollowWrite()
        => Slice(Src("RentNearBy.Infrastructure", "Services", "Social", "FollowService.cs"),
            "public async Task<FollowWriteOutcome> FollowAsync(", "public async Task<bool> UnfollowAsync(");

    [Fact]
    public void Follow_route_runs_the_redis_guard_before_any_db_access()
    {
        var h = FollowHandler();
        Assert.DoesNotContain("SocialHttp.WriteAsync", h);
        Assert.DoesNotContain("ActionLimitAsync", h);
        Assert.DoesNotContain("SocialRateLimits.", h);
        int At(string s) => h.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("BuddyHttp.TryCaller") >= 0);
        Assert.True(At("BuddyHttp.TryCaller") < At("guard.TryAsync("));
        Assert.True(At("guard.TryAsync(") < At("SocialSql.GuardAsync("));
        Assert.True(At("SocialSql.GuardAsync(") < At("follows.FollowAsync("));
        Assert.True(At("follows.FollowAsync(") < At("finally"));
        Assert.True(At("finally") < At("guard.CompleteAsync("));
    }

    [Fact]
    public void TryLater_path_has_no_retry_after_and_no_data()
    {
        var h = FollowHandler();
        Assert.Contains("SocialHttp.Error(SocialErrorTypes.TryLater)", h);
        Assert.DoesNotContain("Retry-After", h);
        Assert.DoesNotContain("RetryAfter", h);
        Assert.DoesNotContain("Retry", Src("RentNearBy.Infrastructure", "Services", "Social", "FollowGuard.cs"));
    }

    [Fact]
    public void Follow_write_makes_no_redis_call_and_no_inline_effects_inside_the_retried_lambda()
    {
        var w = FollowWrite();
        Assert.Contains("PgRetry.OnceAsync(", w);
        foreach (var banned in new[] { "guard", "Guard.", "Redis", "IConnectionMultiplexer", "CountUpSql", "UpsertAsync", "PushAsync", "afterCommit", "FollowerCount", "FollowingCount" })
            Assert.DoesNotContain(banned, w);
        Assert.DoesNotContain("CountUpSql", Src("RentNearBy.Infrastructure", "Services", "Social", "FollowService.cs"));
    }

    [Fact]
    public void Follow_rows_have_one_insert_site()
    {
        var hits = new List<string>();
        foreach (var project in new[] { "RentNearBy.Api", "RentNearBy.Infrastructure" })
        {
            var dir = Path.Combine(BuddySources.Root(), project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                var n = Regex.Matches(text, "INSERT INTO \"Follows\"").Count;
                for (var i = 0; i < n; i++) hits.Add(Path.GetFileName(file));
                Assert.DoesNotContain("Set<Follow>().Add", text);
            }
        }
        Assert.Equal(["FollowService.cs"], hits);
    }

    [Fact]
    public void Lane_files_never_write_the_follow_counters_inline()
    {
        foreach (var file in new[] { "FollowService.cs", "SocialSql.cs", "SocialDeletionParticipant.cs" })
        {
            var text = Src("RentNearBy.Infrastructure", "Services", "Social", file);
            Assert.DoesNotMatch("\"FollowerCount\"\\s*=", text);
            Assert.DoesNotMatch("\"FollowingCount\"\\s*=", text);
        }
        Assert.Contains("INSERT INTO \"FollowCountDirty\"", SocialSql.DeleteFollowSql);
        Assert.Contains("INSERT INTO \"FollowCountDirty\"", Src("RentNearBy.Infrastructure", "Services", "Social", "SocialDeletionParticipant.cs"));
    }

    [Fact]
    public void Accept_sets_EffectsAt_and_unfollow_feeds_churn()
    {
        var service = Src("RentNearBy.Infrastructure", "Services", "Social", "FollowService.cs");
        Assert.Contains("\"EffectsAt\" = COALESCE(\"EffectsAt\", now())", service);
        var handlers = Src("RentNearBy.Api", "Handlers", "Social", "FollowHandlers.cs");
        Assert.Contains("if (await follows.UnfollowAsync(me, userId, ct)) await guard.UnfollowedAsync(me, userId);", handlers);
    }

    [Fact]
    public void Old_24_hour_follow_cap_is_gone()
    {
        Assert.Null(typeof(SocialRateLimits).GetMethod("Follow", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(typeof(SocialLimits).GetField("FollowsPerDay", BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void Guard_is_one_singleton()
        => Assert.Contains("services.AddSingleton(sp => new FollowGuard(", Src("RentNearBy.Api", "Extensions", "SocialFeatureExtensions.cs"));

    // ------------------------------------------------------------------ Lua / C# parity pin

    [Fact]
    public void Lua_has_no_literal_numbers_every_rule_comes_from_FollowGuardRules()
    {
        var source = Src("RentNearBy.Infrastructure", "Services", "Social", "FollowGuard.cs");
        var lua = Slice(source, "private static readonly string Escalate", "private const string StateSql");
        Assert.Empty(Regex.Matches(lua, @"(?<![\w.])\d{2,}").Select(m => m.Value));
    }

    [Fact]
    public void Scripts_render_the_rules()
    {
        Assert.Contains($"ZCARD', KEYS[3]) > {BurstMax} then return escalate(now)", FollowGuard.TryScript);
        Assert.Contains($"ZCARD', KEYS[5]) >= {ChurnMax} then return escalate(now)", FollowGuard.TryScript);
        Assert.Contains($"week < {WeekMax} and ({DayMax} == 0 or day < {DayMax})", FollowGuard.TryScript);
        Assert.Contains($"now - {DayMs} + 1", FollowGuard.TryScript);
        Assert.Contains($"ZCARD', KEYS[4]) >= {HammerMax} then return escalate(now)", FollowGuard.WastedScript);
        Assert.Contains($"local ladder = {{ {Level1Ms}, {Level2Ms}, {Level3Ms} }}", FollowGuard.TryScript);
        Assert.Contains($"math.min(level + 1, {LevelMax})", FollowGuard.WastedScript);
        Assert.Contains($"-{UnfollowedKeep + 1}", FollowGuard.UnfollowedScript);
        Assert.Contains("redis.call('DEL', KEYS[3], KEYS[4], KEYS[5])", FollowGuard.TryScript);
    }

    [Fact]
    public void Try_and_wasted_start_with_the_hydrate_and_block_guards()
    {
        foreach (var script in new[] { FollowGuard.TryScript, FollowGuard.WastedScript })
        {
            var hydrate = script.IndexOf($"if redis.call('EXISTS', KEYS[1]) == 0 then return {{{FollowGuardCodes.Hydrate}, 0, 0}} end", StringComparison.Ordinal);
            var block = script.IndexOf($"return {{{FollowGuardCodes.Blocked}, blockedUntil", StringComparison.Ordinal);
            var firstWrite = script.IndexOf("redis.call('ZADD'", StringComparison.Ordinal);
            Assert.True(hydrate > 0 && block > hydrate && firstWrite > block);
            Assert.True(script.IndexOf("local function escalate(now)", StringComparison.Ordinal) < hydrate);
        }
        Assert.Contains("redis.call('DEL', KEYS[2])", FollowGuard.HydrateScript);
        Assert.Contains("if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end", FollowGuard.HydrateScript);
    }

    [Fact]
    public void Every_escalate_path_exists_in_lua_and_in_the_model()
    {
        Assert.Equal(2, Regex.Matches(FollowGuard.TryScript, @"then return escalate\(now\)").Count);
        Assert.Equal(1, Regex.Matches(FollowGuard.WastedScript, @"then return escalate\(now\)").Count);
        var model = Src("RentNearBy.Core", "Models", "Social", "FollowGuardModel.cs");
        Assert.Equal(3, Regex.Matches(model, @"return Escalate\(now\);").Count);
    }

    private static readonly string[] AllScripts =
        [FollowGuard.TryScript, FollowGuard.WastedScript, FollowGuard.HydrateScript, FollowGuard.UnfollowedScript, FollowGuard.ReleaseScript];

    // The Escalate prefix once ended in 'end' with no newline, so TRY/WASTED read 'endredis.call' and Redis refused to compile them.
    [Fact]
    public void No_lua_keyword_runs_into_the_next_statement()
    {
        foreach (var script in AllScripts)
        {
            Assert.DoesNotMatch(@"\bend[a-zA-Z_]", script);
            Assert.DoesNotMatch(@"\bthen[a-zA-Z_]", script);
        }
        foreach (var script in new[] { FollowGuard.TryScript, FollowGuard.WastedScript })
            Assert.Matches(@"\nend\r?\nredis\.call\(", script);
    }

    [Fact]
    public void Lua_blocks_are_balanced()
    {
        foreach (var script in AllScripts)
        {
            var opens = Regex.Matches(script, @"\b(function|if|for|while)\b").Count;
            var ends = Regex.Matches(script, @"\bend\b").Count;
            Assert.Equal(opens, ends);
        }
    }

    [Fact]
    public void A_missing_block_hash_rehydrates_so_the_ladder_never_drops()
    {
        foreach (var script in new[] { FollowGuard.TryScript, FollowGuard.WastedScript })
        {
            var marker = script.IndexOf("if redis.call('EXISTS', KEYS[1]) == 0", StringComparison.Ordinal);
            var missing = script.IndexOf("if redis.call('EXISTS', KEYS[6]) == 0 then", StringComparison.Ordinal);
            var block = script.IndexOf("local blockedUntil", StringComparison.Ordinal);
            Assert.True(marker > 0 && missing > marker && block > missing);
            var guard = script[missing..block];
            Assert.Contains("redis.call('DEL', KEYS[1])", guard);
            Assert.Contains($"return {{{FollowGuardCodes.Hydrate}, 0, 0}}", guard);
        }
        Assert.Contains($"if redis.call('PTTL', KEYS[6]) < {HydratedTtlMs} then redis.call('PEXPIRE', KEYS[6], {HydratedTtlMs}) end", FollowGuard.TryScript);
        Assert.DoesNotContain("if untilMs > 0 or level > 0", FollowGuard.HydrateScript);
        Assert.Contains("redis.call('HSET', KEYS[6], 'until', untilMs, 'level', level, 'levelAt', levelAt)", FollowGuard.HydrateScript);
        Assert.Contains($", {HydratedTtlMs}))", FollowGuard.HydrateScript);
        Assert.DoesNotContain("redis.call('DEL', KEYS[6])", FollowGuard.HydrateScript);
    }

    [Fact]
    public void Persisted_block_level_takes_the_higher_level()
    {
        var sql = FollowGuard.PersistBlockSql;
        Assert.Contains($"GREATEST(@level, CASE WHEN q.\"FollowBlockLevelAt\" >= @at - interval '1 millisecond' * {LevelForgetMs}", sql);
        Assert.Contains($"LEAST(q.\"FollowBlockLevel\" + 1, {LevelMax}) ELSE 1 END", sql);
        Assert.Contains($"WHEN 1 THEN {Level1Ms} WHEN 2 THEN {Level2Ms} ELSE {Level3Ms} END", sql);
        Assert.Contains("\"FollowBlockUntil\" = GREATEST(@until,", sql);
        Assert.Contains("COALESCE(p.\"FollowBlockUntil\", '-infinity') < @until", sql);
        Assert.DoesNotContain("\"FollowBlockLevel\" = @level", sql);
    }

    [Fact]
    public void Every_script_uses_the_one_keys_order()
    {
        var u = Guid.NewGuid();
        Assert.Equal(
            [FollowGuardKeys.Hydrated(u), FollowGuardKeys.Sends(u), FollowGuardKeys.Attempts(u), FollowGuardKeys.Wasted(u),
             FollowGuardKeys.Churn(u), FollowGuardKeys.Block(u), FollowGuardKeys.Unfollowed(u)],
            FollowGuardKeys.All(u));
        var source = Src("RentNearBy.Infrastructure", "Services", "Social", "FollowGuard.cs");
        Assert.Contains("FollowGuardKeys.All(me)", source);
        Assert.DoesNotMatch(@"KEYS\[[089]\]", FollowGuard.TryScript + FollowGuard.WastedScript + FollowGuard.HydrateScript + FollowGuard.UnfollowedScript);
    }
}
