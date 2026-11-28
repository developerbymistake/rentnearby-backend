using Npgsql;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Core.Models.Social.FollowGuardRules;

namespace RentNearBy.Tests.Social.Discover;

// DESIGN 10.1: the C# reference model of the follow guard Lua (FollowGuardModel), the exact DB decision (FollowWriteRules) and
// the once-only Redis steps (FollowGuardSteps). FollowGuardRedisParityTests runs the same cases against the real scripts.
public class FollowGuardModelTests
{
    private const long T0 = 1_800_000_000_000;
    private const long Hour = 60 * 60 * 1000;

    private static readonly Guid X = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static int _seq;

    private static string Tok() => $"t{Interlocked.Increment(ref _seq)}";

    private static FollowGuardModel Fresh(long now = T0)
    {
        var m = new FollowGuardModel();
        Assert.Equal(1, m.Hydrate(now, 0, 0, 0, []));
        return m;
    }

    private static FollowGuardAnswer Try(FollowGuardModel m, long now, Guid? target = null) => m.Try(now, Tok(), target ?? Guid.NewGuid());

    // Blocks the model now (11 attempts inside one burst window) and returns the answer of the 11th.
    private static FollowGuardAnswer Burst(FollowGuardModel m, long now)
    {
        for (var i = 0; i < BurstMax; i++) Assert.NotEqual(FollowGuardCodes.BlockedNew, Try(m, now).Code);
        return Try(m, now);
    }

    // ------------------------------------------------------------------ week / day

    [Fact]
    public void Week_allows_five_and_the_slot_frees_exactly_seven_days_after_the_oldest()
    {
        var m = Fresh();
        for (var d = 0; d < WeekMax; d++) Assert.Equal(FollowGuardCodes.Reserved, Try(m, T0 + d * DayMs).Code);
        Assert.Equal(FollowGuardCodes.Full, Try(m, T0 + 5 * DayMs).Code);
        Assert.Equal(FollowGuardCodes.Full, Try(m, T0 + WeekMs - 1).Code);
        Assert.Equal(FollowGuardCodes.Reserved, Try(m, T0 + WeekMs).Code);
    }

    [Fact]
    public void Day_allows_three_then_full_with_the_week_free()
    {
        var m = Fresh();
        for (var i = 0; i < DayMax; i++) Assert.Equal(FollowGuardCodes.Reserved, Try(m, T0 + i).Code);
        Assert.Equal(FollowGuardCodes.Full, Try(m, T0 + 3 * Hour).Code);
        Assert.Equal(FollowGuardCodes.Full, Try(m, T0 + DayMs - 1).Code);
        Assert.Equal(FollowGuardCodes.Reserved, Try(m, T0 + DayMs).Code);
        Assert.Equal(DayMax + 1, m.Sends);
    }

    // ------------------------------------------------------------------ burst, ladder, block

    [Fact]
    public void Burst_ten_pass_and_the_eleventh_blocks_for_24_hours_refused_ones_count()
    {
        var m = Fresh();
        var answers = Enumerable.Range(0, BurstMax).Select(_ => Try(m, T0).Code).ToList();
        Assert.Equal(DayMax, answers.Count(c => c == FollowGuardCodes.Reserved));
        Assert.Equal(BurstMax - DayMax, answers.Count(c => c == FollowGuardCodes.Full));
        var blocked = Try(m, T0);
        Assert.Equal(new FollowGuardAnswer(FollowGuardCodes.BlockedNew, T0 + Level1Ms, 1), blocked);
    }

    [Fact]
    public void Burst_window_slides_an_attempt_older_than_60_seconds_leaves_it()
    {
        var m = Fresh();
        for (var i = 0; i < BurstMax; i++) Try(m, T0);
        Assert.NotEqual(FollowGuardCodes.BlockedNew, Try(m, T0 + BurstWindowMs).Code);
    }

    [Fact]
    public void Ladder_is_24h_72h_7d_7d_and_forgets_after_30_days()
    {
        var m = Fresh();
        var now = T0;
        long[] lengths = [Level1Ms, Level2Ms, Level3Ms, Level3Ms];
        for (var n = 0; n < lengths.Length; n++)
        {
            var b = Burst(m, now);
            Assert.Equal(FollowGuardCodes.BlockedNew, b.Code);
            Assert.Equal(now + lengths[n], b.UntilMs);
            Assert.Equal(Math.Min(n + 1, LevelMax), b.Level);
            now = b.UntilMs;
        }
        var later = now + LevelForgetMs + 1;
        Assert.Equal(1, m.Hydrate(later, 0, 0, 0, []));
        var again = Burst(m, later);
        Assert.Equal((1, later + Level1Ms), (again.Level, again.UntilMs));
    }

    [Fact]
    public void An_active_block_refuses_every_try_and_adds_no_attempt()
    {
        var m = Fresh();
        var b = Burst(m, T0);
        for (var i = 0; i < 20; i++)
            Assert.Equal(new FollowGuardAnswer(FollowGuardCodes.Blocked, b.UntilMs, 1), Try(m, T0 + i * 1000));
        // Nothing was recorded during the block: after it, a fresh 10 pass before the next block.
        var after = b.UntilMs;
        for (var i = 0; i < BurstMax; i++) Assert.NotEqual(FollowGuardCodes.BlockedNew, Try(m, after).Code);
        Assert.Equal(FollowGuardCodes.BlockedNew, Try(m, after).Code);
    }

    [Fact]
    public void Nothing_escalates_during_a_block_not_a_refollow_and_not_a_wasted_attempt()
    {
        var m = Fresh();
        m.Unfollowed(T0, X);
        var b = Burst(m, T0);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(FollowGuardCodes.Blocked, m.Try(T0 + i, Tok(), X).Code);
            Assert.Equal(FollowGuardCodes.Blocked, m.Wasted(T0 + i, Tok()).Code);
        }
        Assert.Equal((1, b.UntilMs), (m.Level, m.BlockUntil));
    }

    // ------------------------------------------------------------------ hammer

    [Fact]
    public void Hammer_four_wasted_is_no_block_the_fifth_blocks()
    {
        var m = Fresh();
        for (var i = 0; i < HammerMax - 1; i++) Assert.Equal(FollowGuardCodes.Full, m.Wasted(T0 + i * Hour, Tok()).Code);
        var b = m.Wasted(T0 + 5 * Hour, Tok());
        Assert.Equal(new FollowGuardAnswer(FollowGuardCodes.BlockedNew, T0 + 5 * Hour + Level1Ms, 1), b);
    }

    [Fact]
    public void Hammer_window_is_24_hours()
    {
        var m = Fresh();
        for (var i = 0; i < HammerMax - 1; i++) m.Wasted(T0, Tok());
        Assert.Equal(FollowGuardCodes.Full, m.Wasted(T0 + HammerWindowMs, Tok()).Code);
    }

    [Theory]
    [InlineData(FollowWriteKind.Existing)]
    [InlineData(FollowWriteKind.NotFound)]
    [InlineData(FollowWriteKind.Blocked)]
    [InlineData(FollowWriteKind.FollowingLimit)]
    [InlineData(FollowWriteKind.Full)]
    public void Duplicates_unknown_targets_and_db_refusals_are_wasted(FollowWriteKind kind)
    {
        var now = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        DateTime[] full = [.. Enumerable.Range(1, WeekMax).Select(d => now.AddDays(-d))];
        var d = kind switch
        {
            FollowWriteKind.NotFound => FollowWriteRules.Decide(false, false, null, false, false, [], false, now),
            FollowWriteKind.Existing => FollowWriteRules.Decide(true, true, null, false, false, [], false, now),
            FollowWriteKind.Blocked => FollowWriteRules.Decide(true, false, now.AddHours(1), false, false, [], false, now),
            FollowWriteKind.FollowingLimit => FollowWriteRules.Decide(true, false, null, true, false, [], false, now),
            _ => FollowWriteRules.Decide(true, false, null, false, false, full, true, now),
        };
        Assert.Equal(kind, d.Kind);
        Assert.True(d.Guard.Wasted);
        Assert.False(d.Counted);
    }

    [Fact]
    public void A_successful_follow_back_is_not_wasted_and_not_counted()
    {
        var now = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        DateTime[] full = [.. Enumerable.Range(1, WeekMax).Select(d => now.AddDays(-d))];
        var d = FollowWriteRules.Decide(true, false, null, false, true, full, true, now);
        Assert.Equal((FollowWriteKind.Insert, FollowGuardOutcome.Nothing), (d.Kind, d.Guard));
    }

    // ------------------------------------------------------------------ churn

    [Fact]
    public void Follow_unfollow_follow_is_one_churn_event_and_the_third_blocks()
    {
        var m = Fresh();
        Guid[] people = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        for (var i = 0; i < people.Length; i++)
        {
            var at = T0 + i * DayMs;
            m.Unfollowed(at, people[i]);
            var r = m.Try(at + Hour, Tok(), people[i]);
            if (i < ChurnMax - 1) Assert.NotEqual(FollowGuardCodes.BlockedNew, r.Code);
            else Assert.Equal(new FollowGuardAnswer(FollowGuardCodes.BlockedNew, at + Hour + Level1Ms, 1), r);
        }
    }

    [Fact]
    public void Follow_back_refollows_are_churn_too()
    {
        // Churn is judged in TRY before the week check, so a follow-back (exempt in the DB) still counts as churn.
        var m = Fresh();
        for (var i = 0; i < WeekMax; i++) m.Try(T0 + i * DayMs, Tok(), Guid.NewGuid());
        for (var i = 0; i < ChurnMax; i++)
        {
            var at = T0 + (WeekMax + i) * Hour + 5 * DayMs;
            m.Unfollowed(at, X);
            var r = m.Try(at + 1, Tok(), X);
            Assert.Equal(i < ChurnMax - 1 ? FollowGuardCodes.Full : FollowGuardCodes.BlockedNew, r.Code);
        }
    }

    [Fact]
    public void Unfollowing_accidental_follows_without_refollowing_is_no_churn()
    {
        var m = Fresh();
        for (var i = 0; i < 3; i++) m.Unfollowed(T0 + i, Guid.NewGuid());
        for (var i = 0; i < 3; i++) Assert.Equal(FollowGuardCodes.Reserved, Try(m, T0 + Hour + i).Code);
    }

    [Fact]
    public void A_refollow_after_eight_days_is_no_churn()
    {
        var m = Fresh();
        for (var i = 0; i < ChurnMax; i++)
        {
            var target = Guid.NewGuid();
            var at = T0 + i * 9 * DayMs;
            m.Unfollowed(at, target);
            m.Hydrate(at + 8 * DayMs, 0, 0, 0, []);
            Assert.Equal(FollowGuardCodes.Reserved, m.Try(at + 8 * DayMs, Tok(), target).Code);
        }
    }

    [Fact]
    public void A_block_clears_the_churn_counter_one_more_refollow_after_it_does_not_escalate()
    {
        var m = Fresh();
        Guid[] people = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        for (var i = 0; i < people.Length; i++) m.Unfollowed(T0, people[i]);
        FollowGuardAnswer b = default;
        for (var i = 0; i < ChurnMax; i++) b = m.Try(T0 + i, Tok(), people[i]);
        Assert.Equal(FollowGuardCodes.BlockedNew, b.Code);
        Assert.NotEqual(FollowGuardCodes.BlockedNew, m.Try(b.UntilMs, Tok(), people[3]).Code);
        Assert.Equal(1, m.Level);
    }

    // ------------------------------------------------------------------ hydrate

    [Fact]
    public void Every_script_hydrates_first()
    {
        var m = new FollowGuardModel();
        Assert.Equal(FollowGuardCodes.Hydrate, Try(m, T0).Code);
        Assert.Equal(FollowGuardCodes.Hydrate, m.Wasted(T0, Tok()).Code);
    }

    [Fact]
    public void After_a_flush_a_churn_escalation_continues_the_db_ladder()
    {
        var m = Fresh();
        Burst(m, T0);
        m.Flush();
        var now = T0 + 5 * DayMs;
        Assert.Equal(FollowGuardCodes.Hydrate, m.Try(now, Tok(), X).Code);
        // The DB holds level 2, set 4 days ago, block over.
        Assert.Equal(1, m.Hydrate(now, now - DayMs, 2, now - 4 * DayMs, []));
        Guid[] people = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        FollowGuardAnswer r = default;
        for (var i = 0; i < people.Length; i++)
        {
            m.Unfollowed(now + i, people[i]);
            r = m.Try(now + Hour + i, Tok(), people[i]);
        }
        Assert.Equal(new FollowGuardAnswer(FollowGuardCodes.BlockedNew, now + Hour + 2 + Level3Ms, 3), r);
    }

    [Fact]
    public void Hydrate_keeps_an_active_db_block()
    {
        var m = new FollowGuardModel();
        m.Hydrate(T0, T0 + Hour, 1, T0 - 1, []);
        Assert.Equal(new FollowGuardAnswer(FollowGuardCodes.Blocked, T0 + Hour, 1), Try(m, T0));
    }

    [Fact]
    public void Hydrate_keeps_a_redis_only_block_that_is_longer_than_the_db()
    {
        var m = Fresh();
        var b = Burst(m, T0);
        m.DropMarker();
        m.Hydrate(T0 + 1, 0, 0, 0, []);
        Assert.Equal(new FollowGuardAnswer(FollowGuardCodes.Blocked, b.UntilMs, 1), Try(m, T0 + 2));
    }

    [Fact]
    public void Hydrate_replaces_the_sends_never_merges()
    {
        var m = Fresh();
        var times = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(FollowGuardCodes.Reserved, Try(m, T0 + i * Hour).Code);
            times.Add(T0 + i * Hour);
        }
        m.DropMarker();
        Assert.Equal(1, m.Hydrate(T0 + 4 * Hour, 0, 0, 0, times));
        Assert.Equal(3, m.Sends);
    }

    [Fact]
    public void A_hydrate_after_a_hydrate_is_a_no_op()
    {
        var m = new FollowGuardModel();
        Assert.Equal(1, m.Hydrate(T0, 0, 0, 0, [T0 - DayMs]));
        Assert.Equal(0, m.Hydrate(T0 + 1, 0, 0, 0, [T0, T0 - 1, T0 - 2]));
        Assert.Equal(1, m.Sends);
    }

    [Fact]
    public void Hydrate_drops_times_outside_the_week()
    {
        var m = new FollowGuardModel();
        m.Hydrate(T0, 0, 0, 0, [T0 - WeekMs, T0 - WeekMs + 1, T0 - DayMs]);
        Assert.Equal(2, m.Sends);
    }

    [Fact]
    public void Reseeding_from_the_db_reproduces_the_same_answers()
    {
        var live = Fresh();
        var dbTimes = new List<long>();
        for (var d = 0; d < 4; d++)
            if (live.Try(T0 + d * DayMs, Tok(), Guid.NewGuid()).Code == FollowGuardCodes.Reserved) dbTimes.Add(T0 + d * DayMs);
        var rebuilt = new FollowGuardModel();
        rebuilt.Hydrate(T0 + 4 * DayMs, 0, 0, 0, dbTimes);
        for (var i = 0; i < 4; i++)
        {
            var at = T0 + 4 * DayMs + i * Hour;
            Assert.Equal(live.Try(at, Tok(), Guid.NewGuid()).Code, rebuilt.Try(at, Tok(), Guid.NewGuid()).Code);
        }
    }

    // ------------------------------------------------------------------ release, heal, follow-back

    [Fact]
    public void A_released_token_frees_its_slot_an_unknown_token_is_a_no_op()
    {
        var m = Fresh();
        var token = Tok();
        Assert.Equal(FollowGuardCodes.Reserved, m.Try(T0, token, X).Code);
        m.Release("nobody");
        Assert.Equal(1, m.Sends);
        m.Release(token);
        Assert.Equal(0, m.Sends);
    }

    [Fact]
    public void A_redis_over_count_heals_allowed_counted_marker_dropped_and_not_wasted()
    {
        var now = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        var nowMs = FollowGuardModel.Ms(now);
        var m = Fresh(nowMs - DayMs);
        // A phantom: five tokens Redis holds, none of them committed in the DB.
        for (var d = 0; d < WeekMax; d++) m.Try(nowMs - 6 * DayMs + d * DayMs, Tok(), Guid.NewGuid());
        Assert.Equal(FollowGuardCodes.Full, m.Try(nowMs, Tok(), X).Code);

        var decision = FollowWriteRules.Decide(true, false, null, false, false, [], true, now);
        Assert.Equal(FollowWriteKind.Insert, decision.Kind);
        Assert.Equal(new FollowGuardOutcome(true, true, false), decision.Guard);
        var steps = FollowGuardSteps.Of(false, decision.Guard);
        Assert.Equal(new FollowGuardSteps(false, true, false), steps);

        m.DropMarker();
        Assert.Equal(FollowGuardCodes.Hydrate, m.Try(nowMs + 1, Tok(), X).Code);
        m.Hydrate(nowMs + 1, 0, 0, 0, [nowMs]);
        Assert.Equal(1, m.Sends);
        Assert.Equal(FollowGuardCodes.Reserved, m.Try(nowMs + 2, Tok(), X).Code);
    }

    [Fact]
    public void A_redis_under_count_is_refused_by_the_db_which_drops_the_marker()
    {
        var now = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        DateTime[] full = [.. Enumerable.Range(1, WeekMax).Select(d => now.AddDays(-d))];
        var d = FollowWriteRules.Decide(true, false, null, false, false, full, false, now);
        Assert.Equal(FollowWriteKind.Full, d.Kind);
        Assert.Equal(new FollowGuardSteps(true, true, true), FollowGuardSteps.Of(true, d.Guard));
    }

    [Fact]
    public void Follow_back_with_full_and_exempt_adds_nothing_and_with_reserved_releases()
    {
        var now = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        var followBack = FollowWriteRules.Decide(true, false, null, false, true, [], true, now).Guard;
        Assert.Equal(new FollowGuardSteps(false, false, false), FollowGuardSteps.Of(false, followBack));

        var m = Fresh();
        var token = Tok();
        Assert.Equal(FollowGuardCodes.Reserved, m.Try(T0, token, X).Code);
        var steps = FollowGuardSteps.Of(true, FollowWriteRules.Decide(true, false, null, false, true, [], false, now).Guard);
        Assert.True(steps.Release);
        m.Release(token);
        Assert.Equal(0, m.Sends);
    }

    [Fact]
    public void A_counted_follow_keeps_its_token_and_an_exception_releases_it()
    {
        Assert.Equal(new FollowGuardSteps(false, false, false), FollowGuardSteps.Of(true, new FollowGuardOutcome(true, false, false)));
        Assert.Equal(new FollowGuardSteps(true, false, false), FollowGuardSteps.Of(true, null));
        Assert.Equal(new FollowGuardSteps(false, false, false), FollowGuardSteps.Of(false, null));
    }

    [Fact]
    public void An_active_db_block_refuses_and_drops_the_marker()
    {
        var now = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        var d = FollowWriteRules.Decide(true, false, now.AddSeconds(1), false, true, [], false, now);
        Assert.Equal(FollowWriteKind.Blocked, d.Kind);
        Assert.True(d.Guard.HealMarker);
        Assert.Equal(FollowWriteKind.Insert, FollowWriteRules.Decide(true, false, now, false, true, [], false, now).Kind);
    }

    [Fact]
    public void Db_escalation_uses_the_same_ladder()
    {
        var now = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        Assert.Equal((1, now.AddHours(24)), FollowGuardModel.Escalate(0, null, now));
        Assert.Equal((2, now.AddHours(72)), FollowGuardModel.Escalate(1, now.AddDays(-2), now));
        Assert.Equal((3, now.AddDays(7)), FollowGuardModel.Escalate(3, now.AddDays(-1), now));
        Assert.Equal((1, now.AddHours(24)), FollowGuardModel.Escalate(3, now.AddDays(-31), now));
    }

    // ------------------------------------------------------------------ retry safety

    [Fact]
    public async Task An_outcome_from_a_lambda_that_ran_twice_acts_once()
    {
        var m = Fresh();
        for (var i = 0; i < HammerMax - 2; i++) m.Wasted(T0, Tok());
        var runs = 0;
        var outcome = await PgRetry.OnceAsync(_ =>
        {
            runs++;
            if (runs == 1) throw new PostgresException("deadlock detected", "ERROR", "ERROR", PgRetry.DeadlockDetected);
            return Task.FromResult(new FollowGuardOutcome(false, false, true));
        }, CancellationToken.None);
        Assert.Equal(2, runs);
        var steps = FollowGuardSteps.Of(false, outcome);
        Assert.True(steps.Wasted);
        // One WASTED: HammerMax - 1 in the window, no block (two would have blocked).
        Assert.Equal(FollowGuardCodes.Full, m.Wasted(T0, Tok()).Code);
        Assert.Equal(0, m.Level);
    }

    // ------------------------------------------------------------------ parallel

    [Fact]
    public async Task Fifty_parallel_tries_with_two_used_reserve_exactly_three()
    {
        for (var round = 0; round < 100; round++)
        {
            var m = new FollowGuardModel();
            m.Hydrate(T0, 0, 0, 0, [T0 - 3 * DayMs, T0 - 2 * DayMs]);
            var codes = await RaceAsync(m, 50);
            Assert.Equal(WeekMax - 2, codes.Count(c => c == FollowGuardCodes.Reserved));
            Assert.Equal(1, codes.Count(c => c == FollowGuardCodes.BlockedNew));
            Assert.Equal(WeekMax, m.Sends);
        }
    }

    [Fact]
    public async Task Two_hundred_parallel_tries_from_zero_reserve_exactly_the_day_limit()
    {
        for (var round = 0; round < 100; round++)
        {
            var m = Fresh();
            var codes = await RaceAsync(m, 200);
            Assert.Equal(DayMax, codes.Count(c => c == FollowGuardCodes.Reserved));
            Assert.Equal(1, codes.Count(c => c == FollowGuardCodes.BlockedNew));
            Assert.Equal(DayMax, m.Sends);
        }
    }

    [Fact]
    public async Task Parallel_reserve_and_release_never_leaves_more_than_the_free_slots()
    {
        for (var round = 0; round < 100; round++)
        {
            var m = Fresh();
            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                var token = Tok();
                var at = T0 + i * BurstWindowMs;
                var r = m.Try(at, token, Guid.NewGuid());
                if (r.Code == FollowGuardCodes.Reserved && i % 2 == 0) m.Release(token);
                return r.Code;
            })).ToArray();
            await Task.WhenAll(tasks);
            Assert.True(m.Sends <= DayMax);
            Assert.True(tasks.Count(t => t.Result == FollowGuardCodes.Reserved) >= DayMax);
        }
    }

    private static async Task<List<int>> RaceAsync(FollowGuardModel m, int n)
    {
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, n).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return m.Try(T0, Tok(), Guid.NewGuid()).Code;
        })).ToArray();
        start.Set();
        return [.. await Task.WhenAll(tasks)];
    }
}
