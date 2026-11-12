using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Infrastructure.Services.Retention;

namespace RentNearBy.Tests.Retention;

public class RetentionRunnerTests
{
    private sealed class FakeRule(string name = "Fake", bool exempt = false) : IRetentionRule
    {
        public string Name => name;
        public string Group => RetentionGroups.Notifications;
        public int DefaultDays => 90;
        public bool DefaultEnabled => true;
        public bool GuardExempt => exempt;
        public long Rows { get; set; } = 10;
        public long Candidates { get; set; }
        public long Remaining { get; set; }
        public int FailAtBatch { get; set; }
        public int Calls { get; private set; }
        public List<int> Sizes { get; } = new();
        public Action? OnBatch { get; set; }
        public Task<long> EstimateTableRowsAsync(RetentionContext ctx, CancellationToken ct) => Task.FromResult(Rows);
        public Task<long> CountCandidatesAsync(RetentionContext ctx, long cap, CancellationToken ct) => Task.FromResult(Math.Min(Candidates, cap + 1));
        public Task<int> DeleteBatchAsync(RetentionContext ctx, int batchSize, CancellationToken ct)
        {
            Calls++;
            Sizes.Add(batchSize);
            if (FailAtBatch == Calls) throw new InvalidOperationException("boom");
            OnBatch?.Invoke();
            var n = (int)Math.Min(batchSize, Remaining);
            Remaining -= n;
            return Task.FromResult(n);
        }
    }

    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);
        public int Active;
        public bool Backlog;
        public List<TimeSpan> Delays { get; } = new();
        public int BacklogReads;
        public Func<int>? BacklogUntil;

        public RetentionEnvironment Env(int msPerBatch = 10) => new()
        {
            UtcNow = () => Now,
            Delay = (d, _) => { Delays.Add(d); Now += d; return Task.CompletedTask; },
            ActiveCount = _ => Task.FromResult(Active),
            OutboxBacklog = _ => { BacklogReads++; var b = Backlog; if (BacklogReads >= 3) Backlog = false; return Task.FromResult(b); },
        };
    }

    private static RetentionContext Ctx(int days) => new(null!, DateTime.UtcNow, DateTime.UtcNow.AddDays(-days), new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 12).AddDays(-days), days);

    private static Task<RetentionRuleResult> Run(RetentionOptions o, Clock c, IRetentionRule r, DateTime? slotEnd = null)
        => new RetentionRuleExecutor(o, c.Env(), NullLogger.Instance).RunAsync(r, Ctx, slotEnd ?? c.Now.AddHours(1), default);

    [Fact]
    public async Task Batch_loop_runs_until_nothing_is_left()
    {
        var rule = new FakeRule { Remaining = 2500 };
        var res = await Run(new RetentionOptions(), new Clock(), rule);
        Assert.Equal(RetentionStatus.Completed, res.Status);
        Assert.Equal(2500, res.Deleted);
        Assert.Equal(4, res.Batches); // 1000, 1000, 500, 0
    }

    [Fact]
    public async Task Pause_is_taken_between_batches()
    {
        var clock = new Clock();
        await Run(new RetentionOptions(), clock, new FakeRule { Remaining = 2000 });
        Assert.NotEmpty(clock.Delays);
        Assert.All(clock.Delays, d => Assert.Equal(TimeSpan.FromMilliseconds(300), d));
    }

    [Fact]
    public async Task Disabled_rule_does_nothing()
    {
        var o = new RetentionOptions();
        o.Rules["Fake"] = new RetentionRuleOptions { Enabled = false };
        var rule = new FakeRule { Remaining = 100 };
        var res = await Run(o, new Clock(), rule);
        Assert.Equal(RetentionStatus.Disabled, res.Status);
        Assert.Equal(0, rule.Calls);
    }

    [Fact]
    public async Task Global_off_disables_every_rule()
    {
        var rule = new FakeRule { Remaining = 100 };
        var res = await Run(new RetentionOptions { Enabled = false }, new Clock(), rule);
        Assert.Equal(RetentionStatus.Disabled, res.Status);
        Assert.Equal(0, rule.Calls);
    }

    [Fact]
    public async Task Stops_at_the_window_end_and_next_night_continues()
    {
        var clock = new Clock();
        var rule = new FakeRule { Remaining = 100_000 };
        var slotEnd = clock.Now.AddSeconds(2);
        var first = await Run(new RetentionOptions(), clock, rule, slotEnd);
        Assert.Equal(RetentionStatus.SlotEnd, first.Status);
        Assert.True(first.Batches >= 1 && first.Deleted > 0);
        Assert.True(rule.Remaining > 0);

        var second = await Run(new RetentionOptions(), clock, rule, clock.Now.AddDays(1));
        Assert.Equal(RetentionStatus.Completed, second.Status);
        Assert.Equal(0, rule.Remaining);
    }

    [Fact]
    public async Task Already_past_the_slot_end_deletes_nothing()
    {
        var clock = new Clock();
        var rule = new FakeRule { Remaining = 10 };
        var res = await Run(new RetentionOptions(), clock, rule, clock.Now);
        Assert.Equal(RetentionStatus.SlotEnd, res.Status);
        Assert.Equal(0, rule.Calls);
    }

    [Fact]
    public async Task Safety_stop_deletes_nothing_when_over_the_cap()
    {
        var rule = new FakeRule { Rows = 10_000, Candidates = 2001, Remaining = 2001 };
        var res = await Run(new RetentionOptions(), new Clock(), rule);
        Assert.Equal(RetentionStatus.GuardStop, res.Status);
        Assert.Equal(0, rule.Calls);
    }

    [Fact]
    public async Task Exactly_at_the_cap_still_deletes()
    {
        var rule = new FakeRule { Rows = 10_000, Candidates = 2000, Remaining = 2000 };
        var res = await Run(new RetentionOptions(), new Clock(), rule);
        Assert.Equal(RetentionStatus.Completed, res.Status);
        Assert.Equal(2000, res.Deleted);
    }

    [Fact]
    public async Task Small_tables_exempt_rules_and_the_backlog_escape_skip_the_guard()
    {
        Assert.Equal(RetentionStatus.Completed, (await Run(new RetentionOptions(), new Clock(), new FakeRule { Rows = 500, Candidates = 500, Remaining = 500 })).Status);
        Assert.Equal(RetentionStatus.Completed, (await Run(new RetentionOptions(), new Clock(), new FakeRule(exempt: true) { Rows = 10_000, Candidates = 9000, Remaining = 10 })).Status);
        var o = new RetentionOptions();
        o.Rules["Fake"] = new RetentionRuleOptions { AllowLargeBacklog = true };
        Assert.Equal(RetentionStatus.Completed, (await Run(o, new Clock(), new FakeRule { Rows = 10_000, Candidates = 9000, Remaining = 10 })).Status);
    }

    [Fact]
    public async Task Crash_then_retry_continues_without_losing_or_repeating_work()
    {
        var rule = new FakeRule { Remaining = 3000, FailAtBatch = 2 };
        var first = await Run(new RetentionOptions(), new Clock(), rule);
        Assert.Equal(RetentionStatus.Error, first.Status);
        Assert.Equal(2000, rule.Remaining);

        var second = await Run(new RetentionOptions(), new Clock(), rule);
        Assert.Equal(RetentionStatus.Completed, second.Status);
        Assert.Equal(0, rule.Remaining);
        Assert.Equal(2000, second.Deleted);
    }

    [Fact]
    public async Task Busy_server_uses_the_minimum_batch()
    {
        var clock = new Clock { Active = 8 };
        var rule = new FakeRule { Remaining = 250 };
        await Run(new RetentionOptions(), clock, rule);
        Assert.All(rule.Sizes, s => Assert.Equal(100, s));
    }

    [Fact]
    public async Task Heavy_server_pauses_then_skips_the_rule_for_tonight()
    {
        var clock = new Clock { Active = 15 };
        var rule = new FakeRule { Remaining = 250 };
        var res = await Run(new RetentionOptions(), clock, rule);
        Assert.Equal(RetentionStatus.Busy, res.Status);
        Assert.Equal(0, rule.Calls);
        Assert.Equal(2, clock.Delays.Count(d => d == TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Outbox_backlog_pauses_deletion_until_it_clears()
    {
        var clock = new Clock { Backlog = true };
        var rule = new FakeRule { Remaining = 10 };
        var res = await Run(new RetentionOptions(), clock, rule);
        Assert.Equal(RetentionStatus.Completed, res.Status);
        Assert.True(clock.BacklogReads >= 3);
        Assert.Equal(10, res.Deleted);
    }

    [Fact]
    public async Task Slow_batches_shrink_the_next_batch()
    {
        var clock = new Clock();
        var rule = new FakeRule { Remaining = 5000, OnBatch = () => clock.Now += TimeSpan.FromSeconds(3) };
        await Run(new RetentionOptions(), clock, rule);
        Assert.Equal(1000, rule.Sizes[0]);
        Assert.Equal(500, rule.Sizes[1]);
        Assert.Equal(250, rule.Sizes[2]);
    }

    [Fact]
    public async Task Runner_outside_the_window_or_when_off_never_touches_the_database()
    {
        var scopes = new ThrowingScopes();
        var outside = new FixedClock(new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc));
        var runner = new RetentionRunner(scopes, Options.Create(new RetentionOptions()), new IRetentionRule[] { new FakeRule() }, NullLogger<RetentionRunner>.Instance, outside);
        await runner.TickAsync(default);

        var inside = new FixedClock(IstTime.AtUtc(new DateOnly(2026, 11, 12), 1, 10));
        var off = new RetentionRunner(scopes, Options.Create(new RetentionOptions { Enabled = false }), new IRetentionRule[] { new FakeRule() }, NullLogger<RetentionRunner>.Instance, inside);
        await off.TickAsync(default);
        Assert.Equal(0, scopes.Created);
    }

    [Fact]
    public async Task Runner_marks_a_group_without_rules_done_without_a_scope()
    {
        var scopes = new ThrowingScopes();
        var inside = new FixedClock(IstTime.AtUtc(new DateOnly(2026, 11, 12), 1, 10));
        var runner = new RetentionRunner(scopes, Options.Create(new RetentionOptions()), Array.Empty<IRetentionRule>(), NullLogger<RetentionRunner>.Instance, inside);
        await runner.TickAsync(default);
        Assert.Equal(0, scopes.Created);
    }

    [Fact]
    public async Task Runner_honours_a_narrower_configured_window()
    {
        var scopes = new ThrowingScopes();
        var inside = new FixedClock(IstTime.AtUtc(new DateOnly(2026, 11, 12), 1, 10));
        var o = new RetentionOptions { WindowStartIst = "02:00" };
        var runner = new RetentionRunner(scopes, Options.Create(o), new IRetentionRule[] { new FakeRule() }, NullLogger<RetentionRunner>.Instance, inside);
        await runner.TickAsync(default);
        Assert.Equal(0, scopes.Created);
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private sealed class ThrowingScopes : IServiceScopeFactory
    {
        public int Created;
        public IServiceScope CreateScope() { Created++; throw new InvalidOperationException("no database in unit tests"); }
    }
}
