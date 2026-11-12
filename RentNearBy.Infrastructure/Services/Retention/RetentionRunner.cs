using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Retention;

public sealed record RetentionRuleResult(string Rule, string Group, RetentionStatus Status, long Deleted, int Batches, long DurationMs, DateTime CutoffUtc);

// Everything the batch loop needs from the outside world, so tests run without PostgreSQL.
public sealed class RetentionEnvironment
{
    public required Func<DateTime> UtcNow { get; init; }
    public required Func<TimeSpan, CancellationToken, Task> Delay { get; init; }
    public required Func<CancellationToken, Task<int>> ActiveCount { get; init; }
    public required Func<CancellationToken, Task<bool>> OutboxBacklog { get; init; }
}

// Runs one rule: config, safety stop, then batches until done, slot end, heavy load or an error.
public sealed class RetentionRuleExecutor(RetentionOptions options, RetentionEnvironment env, ILogger log)
{
    public async Task<RetentionRuleResult> RunAsync(IRetentionRule rule, Func<int, RetentionContext> contextFactory, DateTime slotEndUtc, CancellationToken ct)
    {
        var started = env.UtcNow();
        var settings = options.Resolve(rule.Name, rule.DefaultDays, rule.DefaultEnabled);
        long deleted = 0;
        var batches = 0;
        var cutoff = started.AddDays(-settings.Days);
        var status = RetentionStatus.Completed;
        try
        {
            if (!settings.Enabled) return Done(RetentionStatus.Disabled);
            if (started >= slotEndUtc) return Done(RetentionStatus.SlotEnd);

            var ctx = contextFactory(settings.Days);
            cutoff = ctx.CutoffUtc;

            if (!rule.GuardExempt && !settings.AllowLargeBacklog)
            {
                var rows = await rule.EstimateTableRowsAsync(ctx, ct);
                if (RetentionSafetyGuard.Applies(rows, options.GuardMinRows, rule.GuardExempt, settings.AllowLargeBacklog))
                {
                    var cap = RetentionSafetyGuard.Cap(rows, options.MaxPercent);
                    var candidates = await rule.CountCandidatesAsync(ctx, cap, ct);
                    if (RetentionSafetyGuard.IsOverCap(candidates, cap))
                    {
                        log.LogError("RETENTION_ALERT rule={Rule} reason=GuardStop candidates={Candidates} cap={Cap} tableRows={Rows} hint=Retention:Rules:{Rule}:AllowLargeBacklog",
                            rule.Name, candidates, cap, rows);
                        return Done(RetentionStatus.GuardStop);
                    }
                }
            }

            var batch = options.BatchSize;
            var pause = options.PauseMs;
            var heavyPauses = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (env.UtcNow() >= slotEndUtc) { status = RetentionStatus.SlotEnd; break; }

                var load = RetentionBusyGuard.Classify(await env.ActiveCount(ct), options);
                if (load == RetentionLoad.Heavy)
                {
                    if (++heavyPauses >= RetentionBusyGuard.MaxHeavyPauses) { status = RetentionStatus.Busy; break; }
                    await env.Delay(TimeSpan.FromMilliseconds(RetentionBusyGuard.HeavyPauseMs), ct);
                    continue;
                }
                if (await env.OutboxBacklog(ct))
                {
                    await env.Delay(TimeSpan.FromMilliseconds(pause), ct);
                    continue;
                }

                var size = RetentionBusyGuard.BatchFor(load, batch, options);
                var t0 = env.UtcNow();
                var n = await rule.DeleteBatchAsync(ctx, size, ct);
                var ms = (long)(env.UtcNow() - t0).TotalMilliseconds;
                batches++;
                deleted += n;
                if (n == 0) break;

                batch = RetentionBusyGuard.NextBatch(batch, ms, options);
                pause = RetentionBusyGuard.NextPause(pause, ms, options);
                await env.Delay(TimeSpan.FromMilliseconds(pause), ct);
            }
            return Done(status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Retention rule {Rule} failed after {Deleted} rows", rule.Name, deleted);
            return Done(RetentionStatus.Error);
        }

        RetentionRuleResult Done(RetentionStatus s)
        {
            var ms = (long)(env.UtcNow() - started).TotalMilliseconds;
            log.LogInformation("Retention rule={Rule} group={Group} status={Status} deleted={Deleted} batches={Batches} durationMs={Ms} cutoff={CutoffUtc:o}",
                rule.Name, rule.Group, s, deleted, batches, ms, cutoff);
            return new RetentionRuleResult(rule.Name, rule.Group, s, deleted, batches, ms, cutoff);
        }
    }
}

// Thin 60 s tick: finds the due slot, takes the global advisory lock per rule, runs the rules of the slot.
public sealed class RetentionRunner : BackgroundService
{
    public const string LockName = "retention.run";
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly RetentionOptions _options;
    private readonly IReadOnlyList<IRetentionRule> _rules;
    private readonly ILogger<RetentionRunner> _log;
    private readonly TimeProvider _clock;
    private readonly HashSet<string> _doneGroups = new();
    private readonly HashSet<string> _doneRules = new();
    private DateOnly _day;

    public RetentionRunner(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, IEnumerable<IRetentionRule> rules,
        ILogger<RetentionRunner> log, TimeProvider? clock = null)
    {
        _scopes = scopes;
        _options = options.Value;
        _rules = rules.Select((r, i) => (r, i)).OrderBy(x => RetentionSchedule.OrderOf(x.r.Name)).ThenBy(x => x.i).Select(x => x.r).ToList();
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Tick, _clock);
            do
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Retention tick failed"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    // One tick. Public so tests can drive it with a fake clock.
    public async Task TickAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return;
        var now = _clock.GetUtcNow().UtcDateTime;
        var date = IstTime.TodayIst(now);
        if (date != _day) { _day = date; _doneGroups.Clear(); _doneRules.Clear(); }

        var slot = RetentionSchedule.DueSlot(now, _doneGroups);
        if (slot is null) return;
        var windowStart = IstTime.AtUtc(date, ParseHour(_options.WindowStartIst, 1), ParseMinute(_options.WindowStartIst, 0));
        var windowEnd = IstTime.AtUtc(date, ParseHour(_options.WindowEndIst, 4), ParseMinute(_options.WindowEndIst, 30));
        if (now < windowStart || now >= windowEnd) return;
        var slotEnd = Min(RetentionSchedule.SlotEndUtc(slot, date), windowEnd);

        var groupRules = _rules.Where(r => r.Group == slot.Group && !_doneRules.Contains(r.Name)).ToList();
        if (groupRules.Count == 0) { _doneGroups.Add(slot.Group); return; }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connectionString = db.Database.GetConnectionString() ?? throw new InvalidOperationException("No connection string");
        var env = new RetentionEnvironment
        {
            UtcNow = () => _clock.GetUtcNow().UtcDateTime,
            Delay = (d, c) => Task.Delay(d, _clock, c),
            ActiveCount = c => db.Database.SqlQueryRaw<long>(
                "SELECT count(*) AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND state = 'active' AND pid <> pg_backend_pid()")
                .SingleAsync(c).ContinueWith(t => (int)t.Result, c),
            OutboxBacklog = async c =>
            {
                var older = _clock.GetUtcNow().UtcDateTime - RetentionBusyGuard.OutboxBacklogAge;
                var n = await db.Database.SqlQuery<long>(
                    $"SELECT count(*) AS \"Value\" FROM (SELECT 1 FROM \"OutboxMessages\" WHERE \"PublishedAt\" IS NULL AND \"CreatedAt\" < {older} LIMIT 1) x").SingleAsync(c);
                return n > 0;
            },
        };
        var executor = new RetentionRuleExecutor(_options, env, _log);

        var lockBusy = false;
        foreach (var rule in groupRules)
        {
            ct.ThrowIfCancellationRequested();
            if (_clock.GetUtcNow().UtcDateTime >= slotEnd) break;
            await using var gate = await PgAdvisoryLock.TryAcquireAsync(connectionString, LockName, ct);
            if (gate is null)
            {
                lockBusy = true;
                _log.LogInformation("Retention rule={Rule} group={Group} status={Status} deleted=0 batches=0 durationMs=0 cutoff={CutoffUtc:o}",
                    rule.Name, rule.Group, RetentionStatus.LockBusy, now);
                continue;
            }
            var result = await executor.RunAsync(rule, days => BuildContext(db, days), slotEnd, ct);
            if (result.Status != RetentionStatus.SlotEnd) _doneRules.Add(rule.Name);
        }
        // LockBusy retries next tick until the slot ends; everything else is done for tonight.
        if (!lockBusy || _clock.GetUtcNow().UtcDateTime >= slotEnd) _doneGroups.Add(slot.Group);
    }

    private RetentionContext BuildContext(ApplicationDbContext db, int days)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var today = IstTime.TodayIst(now);
        return new RetentionContext(db, now, now.AddDays(-days), today, today.AddDays(-days), days);
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static int ParseHour(string s, int fallback) => TimeOnly.TryParse(s, out var t) ? t.Hour : fallback;
    private static int ParseMinute(string s, int fallback) => TimeOnly.TryParse(s, out var t) ? t.Minute : fallback;
}
