using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Infrastructure.Services.Buddy;

// Contract section 6: one hosted service runs every registered IBuddyFeatureJob, one instance at a time (advisory lock),
// each job in its own scope; a failing job logs and never stops the others.
public sealed class BuddyFeatureJobsService(
    IServiceScopeFactory scopes,
    DbLockConnectionString lockConnection,
    IOptions<BuddyFeatureOptions> options,
    TimeProvider clock,
    ILogger<BuddyFeatureJobsService> logger) : BackgroundService
{
    public const string LockName = "buddy-feature-jobs";

    private readonly Dictionary<string, DateTime> _lastRunUtc = new();
    private readonly Dictionary<string, DateOnly> _lastDailyIst = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.JobsEnabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, o.JobsFirstRunDelayMinutes)), clock, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, o.JobsTickMinutes)), clock);
            do
            {
                await TickAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await using var gate = await PgAdvisoryLock.TryAcquireAsync(lockConnection.Value, LockName, ct);
            if (gate is null) return;

            List<(string Name, TimeSpan Interval, TimeOnly? DailyAtIst)> jobs;
            using (var scope = scopes.CreateScope())
                jobs = scope.ServiceProvider.GetServices<IBuddyFeatureJob>().Select(j => (j.Name, j.Interval, j.DailyAtIst)).ToList();

            foreach (var job in jobs) // batch-loop
            {
                ct.ThrowIfCancellationRequested();
                var now = clock.GetUtcNow().UtcDateTime;
                if (!IsDue(job.Name, job.Interval, job.DailyAtIst, now)) continue;
                await RunOneAsync(job.Name, now, job.DailyAtIst != null, ct); // batch-loop
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "buddy.jobs tick failed");
        }
    }

    internal bool IsDue(string name, TimeSpan interval, TimeOnly? dailyAtIst, DateTime nowUtc)
    {
        if (dailyAtIst is { } at)
        {
            var today = IstTime.TodayIst(nowUtc);
            if (_lastDailyIst.TryGetValue(name, out var last) && last >= today) return false;
            return IstTime.TimeOfDayIst(nowUtc) >= at.ToTimeSpan();
        }
        return !_lastRunUtc.TryGetValue(name, out var lastRun) || nowUtc - lastRun >= interval;
    }

    private async Task RunOneAsync(string name, DateTime nowUtc, bool daily, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var scope = scopes.CreateScope();
            var job = scope.ServiceProvider.GetServices<IBuddyFeatureJob>().First(j => j.Name == name);
            var rows = await job.RunAsync(ct);
            logger.LogInformation("buddy.job {Name} {Rows} {Ms}", name, rows, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "buddy.job {Name} failed after {Ms}", name, sw.ElapsedMilliseconds);
        }
        finally
        {
            _lastRunUtc[name] = nowUtc;
            if (daily) _lastDailyIst[name] = IstTime.TodayIst(nowUtc);
        }
    }
}
