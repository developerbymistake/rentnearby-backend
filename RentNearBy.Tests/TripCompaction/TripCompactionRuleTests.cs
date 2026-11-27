using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.GroupB;

namespace RentNearBy.Tests.TripCompaction;

// Trip compaction rule: dry run writes nothing, batches, one bad trip never blocks the rest, registration in the schedule,
// the exact eligibility SQL, and the guard that the level count and CompletionCountedAt are never touched.
public class TripCompactionRuleTests
{
    private sealed class FakeStore : ITripCompactionStore
    {
        public List<Guid> Pending { get; } = new();
        public HashSet<Guid> Poison { get; } = new();
        // Held by another worker (SKIP LOCKED) or no longer eligible: ArchiveAsync answers "not archived" without throwing.
        public HashSet<Guid> Locked { get; } = new();
        public List<Guid> Archived { get; } = new();
        public int CountCalls;
        public int BatchCalls;
        public int ArchiveCalls;

        public Task<long> TableRowsAsync(RetentionContext ctx, CancellationToken ct) => Task.FromResult(10_000L);
        public Task<long> CountCappedAsync(RetentionContext ctx, long cap, CancellationToken ct) => Task.FromResult(Math.Min(Pending.Count, cap + 1));

        public Task<TripCompactionCounts> CountAsync(RetentionContext ctx, CancellationToken ct)
        {
            CountCalls++;
            return Task.FromResult(new TripCompactionCounts(Pending.Count, Pending.Count * 5000L, 3));
        }

        public Task<IReadOnlyList<Guid>> NextBatchAsync(RetentionContext ctx, int n, IReadOnlyCollection<Guid> skip, CancellationToken ct)
        {
            BatchCalls++;
            return Task.FromResult<IReadOnlyList<Guid>>(Pending.Where(id => !skip.Contains(id)).Take(n).ToList());
        }

        public Task<TripCompactionOutcome> ArchiveAsync(RetentionContext ctx, Guid tripId, CancellationToken ct)
        {
            ArchiveCalls++;
            if (Poison.Contains(tripId)) throw new InvalidOperationException("bad plan");
            if (Locked.Contains(tripId)) return Task.FromResult(new TripCompactionOutcome(false, 0));
            Pending.Remove(tripId);
            Archived.Add(tripId);
            return Task.FromResult(new TripCompactionOutcome(true, 4000));
        }
    }

    private static RetentionContext Ctx(bool dryRun) => new RetentionContext(null!, DateTime.UtcNow, DateTime.UtcNow.AddDays(-90),
        new DateOnly(2026, 11, 27), new DateOnly(2026, 8, 29), 90) { DryRun = dryRun, Log = NullLogger.Instance };

    private static async Task<int> RunAll(TripCompactionRule rule, RetentionContext ctx, int batch = 1000)
    {
        var total = 0;
        for (var guard = 0; guard < 100; guard++)
        {
            var n = await rule.DeleteBatchAsync(ctx, batch, CancellationToken.None);
            if (n == 0) return total;
            total += n;
        }
        throw new InvalidOperationException("the rule never returned 0");
    }

    [Fact]
    public async Task Dry_run_counts_once_and_writes_nothing()
    {
        var store = new FakeStore();
        store.Pending.AddRange(Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()));
        var rule = new TripCompactionRule(_ => store);

        var archived = await RunAll(rule, Ctx(dryRun: true));

        Assert.Equal(0, archived);
        Assert.Equal(1, store.CountCalls);
        Assert.Equal(0, store.BatchCalls);
        Assert.Equal(0, store.ArchiveCalls);
        Assert.Equal(40, store.Pending.Count);
    }

    [Fact]
    public async Task Live_run_archives_every_eligible_trip_in_small_batches()
    {
        var store = new FakeStore();
        store.Pending.AddRange(Enumerable.Range(0, 60).Select(_ => Guid.NewGuid()));
        var rule = new TripCompactionRule(_ => store);

        var archived = await RunAll(rule, Ctx(dryRun: false));

        Assert.Equal(60, archived);
        Assert.Empty(store.Pending);
        // 1000 from the executor is clamped to TripBatch per call.
        Assert.True(store.BatchCalls >= 60 / TripCompactionRule.TripBatch);
        Assert.Equal(1, store.CountCalls);
    }

    [Fact]
    public async Task One_failing_trip_is_skipped_for_the_run_and_the_rest_continue()
    {
        var store = new FakeStore();
        var bad = Guid.NewGuid();
        store.Pending.Add(bad);
        store.Poison.Add(bad);
        store.Pending.AddRange(Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()));
        var rule = new TripCompactionRule(_ => store);

        var archived = await RunAll(rule, Ctx(dryRun: false));

        Assert.Equal(30, archived);
        Assert.Equal([bad], store.Pending);
        Assert.Equal(1, store.ArchiveCalls - 30);
    }

    [Fact]
    public async Task Only_poisoned_trips_end_the_run_without_looping()
    {
        var store = new FakeStore();
        var bad = Guid.NewGuid();
        store.Pending.Add(bad);
        store.Poison.Add(bad);
        var rule = new TripCompactionRule(_ => store);

        Assert.Equal(0, await RunAll(rule, Ctx(dryRun: false)));
        Assert.Equal(1, store.ArchiveCalls);
    }

    [Fact]
    public async Task A_full_batch_of_failing_trips_at_the_head_never_starves_the_trips_behind_it()
    {
        var store = new FakeStore();
        // More always-failing trips than one batch, first in the EndDate order, then good ones.
        var bad = Enumerable.Range(0, TripCompactionRule.TripBatch + 5).Select(_ => Guid.NewGuid()).ToList();
        store.Pending.AddRange(bad);
        foreach (var id in bad) store.Poison.Add(id);
        var good = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        store.Pending.AddRange(good);
        var rule = new TripCompactionRule(_ => store);

        var archived = await RunAll(rule, Ctx(dryRun: false));

        Assert.Equal(10, archived);
        Assert.Equal(good.OrderBy(x => x), store.Archived.OrderBy(x => x));
        Assert.Equal(bad.OrderBy(x => x), store.Pending.OrderBy(x => x));
        // Each bad trip was tried exactly once in the run.
        Assert.Equal(bad.Count + good.Count, store.ArchiveCalls);
    }

    [Fact]
    public async Task Trips_locked_by_another_worker_are_skipped_once_and_the_rest_continue()
    {
        var store = new FakeStore();
        var locked = Enumerable.Range(0, TripCompactionRule.TripBatch).Select(_ => Guid.NewGuid()).ToList();
        store.Pending.AddRange(locked);
        foreach (var id in locked) store.Locked.Add(id);
        var good = Guid.NewGuid();
        store.Pending.Add(good);
        var rule = new TripCompactionRule(_ => store);

        Assert.Equal(1, await RunAll(rule, Ctx(dryRun: false)));
        Assert.Equal([good], store.Archived);
        Assert.Equal(locked.Count + 1, store.ArchiveCalls);
    }

    [Fact]
    public async Task Only_locked_trips_end_the_run_without_looping()
    {
        var store = new FakeStore();
        var id = Guid.NewGuid();
        store.Pending.Add(id);
        store.Locked.Add(id);
        var rule = new TripCompactionRule(_ => store);

        Assert.Equal(0, await RunAll(rule, Ctx(dryRun: false)));
        Assert.Equal(1, store.ArchiveCalls);
        Assert.Empty(store.Archived);
    }

    [Fact]
    public async Task A_new_run_starts_fresh()
    {
        var store = new FakeStore();
        store.Pending.Add(Guid.NewGuid());
        var rule = new TripCompactionRule(_ => store);
        Assert.Equal(1, await RunAll(rule, Ctx(dryRun: false)));
        store.Pending.Add(Guid.NewGuid());
        Assert.Equal(1, await RunAll(rule, Ctx(dryRun: false)));
        Assert.Equal(2, store.CountCalls);
    }

    [Fact]
    public void Rule_is_discovered_and_scheduled_in_the_housekeeping_slot_with_safe_defaults()
    {
        var rule = Assert.Single(RetentionRuleRegistration.Discover(typeof(RetentionRunner).Assembly), r => r.Name == TripCompactionRule.RuleName);
        Assert.Equal(RetentionGroups.TripHousekeeping, rule.Group);
        Assert.Contains(RetentionSchedule.Slots, s => s.Group == rule.Group);
        Assert.True(RetentionSchedule.OrderOf(TripCompactionRule.RuleName) < RetentionSchedule.RuleOrder.Count);
        Assert.Equal(90, rule.DefaultDays);
        Assert.True(rule.DefaultEnabled);
        Assert.False(rule.GuardExempt);
        Assert.True(Assert.IsAssignableFrom<IDryRunRetentionRule>(rule).DefaultDryRun);
    }

    [Fact]
    public void Dry_run_is_the_default_and_config_switches_it_off_or_kills_the_rule()
    {
        var none = new RetentionOptions();
        none.Normalize();
        Assert.True(none.Resolve(TripCompactionRule.RuleName, 90, true, defaultDryRun: true).DryRun);

        var live = new RetentionOptions { Rules = { [TripCompactionRule.RuleName] = new RetentionRuleOptions { DryRun = false } } };
        live.Normalize();
        Assert.False(live.Resolve(TripCompactionRule.RuleName, 90, true, defaultDryRun: true).DryRun);

        var off = new RetentionOptions { Rules = { [TripCompactionRule.RuleName] = new RetentionRuleOptions { Enabled = false } } };
        off.Normalize();
        Assert.False(off.Resolve(TripCompactionRule.RuleName, 90, true, defaultDryRun: true).Enabled);

        // Other rules never become dry runs.
        Assert.False(none.Resolve("Chat", 90, true).DryRun);
    }

    private sealed class CapturingRule : IDryRunRetentionRule
    {
        public bool? SeenDryRun;
        public bool SawLog;
        public string Name => TripCompactionRule.RuleName;
        public string Group => RetentionGroups.TripHousekeeping;
        public int DefaultDays => 90;
        public bool DefaultEnabled => true;
        public bool GuardExempt => true;
        public bool DefaultDryRun => true;
        public Task<long> EstimateTableRowsAsync(RetentionContext ctx, CancellationToken ct) => Task.FromResult(0L);
        public Task<long> CountCandidatesAsync(RetentionContext ctx, long cap, CancellationToken ct) => Task.FromResult(0L);
        public Task<int> DeleteBatchAsync(RetentionContext ctx, int batchSize, CancellationToken ct)
        {
            SeenDryRun = ctx.DryRun;
            SawLog = ctx.Log != null;
            return Task.FromResult(0);
        }
    }

    private static RetentionEnvironment Env() => new()
    {
        UtcNow = () => new DateTime(2026, 11, 26, 20, 30, 0, DateTimeKind.Utc),
        Delay = (_, _) => Task.CompletedTask,
        ActiveCount = _ => Task.FromResult(0),
        OutboxBacklog = _ => Task.FromResult(false),
    };

    private static RetentionContext Plain(int days) => new(null!, DateTime.UtcNow, DateTime.UtcNow.AddDays(-days), new DateOnly(2026, 11, 27),
        new DateOnly(2026, 11, 27).AddDays(-days), days);

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Executor_passes_the_resolved_dry_run_into_the_context(bool? configured, bool expected)
    {
        var options = new RetentionOptions();
        if (configured is { } c) options.Rules[TripCompactionRule.RuleName] = new RetentionRuleOptions { DryRun = c };
        options.Normalize();
        var rule = new CapturingRule();

        await new RetentionRuleExecutor(options, Env(), NullLogger.Instance).RunAsync(rule, Plain, new DateTime(2026, 11, 26, 21, 0, 0, DateTimeKind.Utc), default);

        Assert.Equal(expected, rule.SeenDryRun);
        Assert.True(rule.SawLog);
    }

    // Guarded rule (GuardExempt false) whose candidates are far over the 20 % cap of a big table.
    private sealed class BigBacklogRule : IDryRunRetentionRule
    {
        public int Batches;
        public string Name => TripCompactionRule.RuleName;
        public string Group => RetentionGroups.TripHousekeeping;
        public int DefaultDays => 90;
        public bool DefaultEnabled => true;
        public bool GuardExempt => false;
        public bool DefaultDryRun => true;
        public Task<long> EstimateTableRowsAsync(RetentionContext ctx, CancellationToken ct) => Task.FromResult(10_000L);
        public Task<long> CountCandidatesAsync(RetentionContext ctx, long cap, CancellationToken ct) => Task.FromResult(cap + 1);
        public Task<int> DeleteBatchAsync(RetentionContext ctx, int batchSize, CancellationToken ct)
        {
            Batches++;
            return Task.FromResult(0);
        }
    }

    [Theory]
    [InlineData(true, RetentionStatus.Completed, 1)]
    [InlineData(false, RetentionStatus.GuardStop, 0)]
    public async Task Backlog_guard_stops_a_live_run_but_never_hides_the_dry_run_count(bool dryRun, RetentionStatus expected, int batches)
    {
        var options = new RetentionOptions();
        options.Rules[TripCompactionRule.RuleName] = new RetentionRuleOptions { DryRun = dryRun };
        options.Normalize();
        var rule = new BigBacklogRule();

        var result = await new RetentionRuleExecutor(options, Env(), NullLogger.Instance)
            .RunAsync(rule, Plain, new DateTime(2026, 11, 26, 21, 0, 0, DateTimeKind.Utc), default);

        Assert.Equal(expected, result.Status);
        Assert.Equal(batches, rule.Batches);
    }

    [Fact]
    public void Eligibility_sql_has_every_protection()
    {
        var sql = TripCompactionStore.EligibleSql;
        Assert.Contains("t.\"ArchivedAt\" IS NULL", sql);
        Assert.Contains("t.\"EndDate\" IS NOT NULL", sql);
        Assert.Contains("t.\"EndDate\" < @cutoffDate", sql);
        Assert.Contains("t.\"Status\" IN ('Completed','Planned')", sql);
        Assert.DoesNotContain("'Draft'", sql);
        Assert.Contains("\"TripEnquiries\"", sql);
        Assert.Contains("'Asked','Offered','Countered','FareSent'", sql);
        Assert.Contains("\"TaxiRequests\"", sql);
        Assert.Contains("\"TaxiBookings\"", sql);
        Assert.Contains("\"ListingReports\"", sql);
        // A group chat still in use (a message in the last Days) keeps the trip waiting.
        Assert.Contains("g.\"Kind\" = 'T' AND g.\"LastMessageAt\" >= @cutoff", sql);
        Assert.Contains("SKIP LOCKED", TripCompactionStore.LockSql);
        Assert.Contains("ORDER BY t.\"EndDate\"", TripCompactionStore.NextBatchSql);
        Assert.Contains("LIMIT @n", TripCompactionStore.NextBatchSql);
        Assert.Contains("\"DraftsWithoutEnd\"", TripCompactionStore.CountSql);
        // Each statement is scanned on its own: joined end to end, NextBatchSql's closing "@n" ran into LockSql's "SELECT" and
        // the regex read one parameter "@nSELECT" that no statement contains.
        string[] statements = [sql, TripCompactionStore.NextBatchSql, TripCompactionStore.LockSql, TripCompactionStore.CountSql, TripCompactionStore.CappedCountSql(10)];
        foreach (var statement in statements)
        {
            // Only the allowed retention parameters (plus the batch seams @skip and @trip).
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(statement, @"@\w+"))
                Assert.True(RetentionParams.Allowed.Contains(m.Value) || m.Value is "@skip" or "@trip", m.Value);
            // SqlQueryRaw formats the SQL: no brace may reach it.
            Assert.DoesNotContain("{", statement);
        }
    }

    [Fact]
    public void Compaction_never_names_the_level_count_or_completion_columns()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "RentNearBy.Infrastructure", "Services", "Retention", "GroupB", "TripCompactionRule.cs"));
        var builder = File.ReadAllText(Path.Combine(root, "RentNearBy.Core", "Models", "TripArchiveModels.cs"));
        foreach (var text in new[] { source, builder, TripCompactionStore.EligibleSql, TripCompactionStore.LockSql })
        {
            Assert.DoesNotContain("CompletionCountedAt", text);
            Assert.DoesNotContain("CompletedTripsCount", text);
            Assert.DoesNotContain("\"Users\"", text);
            Assert.DoesNotContain("SetProperty(t => t.Completion", text);
            Assert.DoesNotContain("SetProperty(t => t.Status", text);
        }
        // The archive writes exactly these Trips columns.
        var sets = System.Text.RegularExpressions.Regex.Matches(source, @"SetProperty\(t => t\.(\w+)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["SummaryJson", "PlanJson", "ArchivedAt", "IsPublic", "PausedByPlan", "UpdatedAt"], sets);
    }
}
