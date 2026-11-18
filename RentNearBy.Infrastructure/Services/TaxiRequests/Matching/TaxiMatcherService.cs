using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

public interface IMatcherSweepSource
{
    Task<IReadOnlyList<long>> DueCitiesAsync(CancellationToken ct);
    Task<IReadOnlyList<long>> CitiesOfDriversAsync(IReadOnlyCollection<Guid> driverListingIds, CancellationToken ct);
    Task<int> CountStuckAsync(CancellationToken ct);
}

public sealed record MatcherServiceTimings(
    TimeSpan SweepEvery, TimeSpan StuckEvery, TimeSpan LockBusyBackoff, TimeSpan FailedPassBackoff, TimeSpan ErrorPause,
    int Parallelism, int MaxInlineReruns)
{
    public static readonly MatcherServiceTimings Default = new(
        TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1),
        Parallelism: 4, MaxInlineReruns: 3);
}

// Drains dirty cities with bounded parallelism, reruns a city that was marked again during its pass (lost wake-up fix),
// sweeps every 2 s for cities the signals missed and skips everything while the master switch is off. A failed pass is
// logged (7120) and retried later; nothing but cancellation leaves ExecuteAsync.
public sealed class TaxiMatcherService : BackgroundService
{
    public const string HeartbeatName = "matcher";

    private readonly ITaxiMatcherPass _pass;
    private readonly IMatcherSweepSource _sweep;
    private readonly MatcherSignal _signal;
    private readonly ITripExecutionFeatureReader _feature;
    private readonly ILogger _logger;
    private readonly WorkerHeartbeats? _heartbeats;
    private readonly TimeProvider _clock;
    private readonly MatcherServiceTimings _timings;
    private readonly ConcurrentDictionary<long, DateTimeOffset> _heldBack = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private DateTimeOffset _nextStuck = DateTimeOffset.MinValue;

    public TaxiMatcherService(
        IServiceScopeFactory scopes, MatcherSignal signal, ITripExecutionFeatureReader feature, ILogger<TaxiMatcherService> logger,
        WorkerHeartbeats? heartbeats = null)
        : this(new ScopedMatcherPass(scopes), new PostgresMatcherSweepSource(scopes), signal, feature, logger, heartbeats,
            TimeProvider.System, MatcherServiceTimings.Default)
    {
    }

    internal TaxiMatcherService(
        ITaxiMatcherPass pass, IMatcherSweepSource sweep, MatcherSignal signal, ITripExecutionFeatureReader feature, ILogger logger,
        WorkerHeartbeats? heartbeats, TimeProvider clock, MatcherServiceTimings timings)
    {
        _pass = pass;
        _sweep = sweep;
        _signal = signal;
        _feature = feature;
        _logger = logger;
        _heartbeats = heartbeats;
        _clock = clock;
        _timings = timings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _heartbeats?.Beat(HeartbeatName);
                var sleep = await RunOnceAsync(stoppingToken);
                if (sleep > TimeSpan.Zero) await _signal.WaitAsync(sleep, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Taxi matcher loop failed");
                try
                {
                    await Task.Delay(_timings.ErrorPause, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    // One drain. Returns how long to sleep; a mark made while it ran keeps the wake set (Reset comes first), so it is not lost.
    internal async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        _signal.Reset();
        var now = _clock.GetUtcNow();

        if (!await _feature.IsEnabledAsync())
        {
            _signal.TakeFreedDrivers();
            _nextSweep = DateTimeOffset.MinValue;
            return _timings.SweepEvery;
        }

        var freed = _signal.TakeFreedDrivers();
        if (freed.Count > 0)
        {
            try
            {
                foreach (var city in await _sweep.CitiesOfDriversAsync(freed, ct)) _signal.MarkDirty(city);
            }
            catch
            {
                foreach (var id in freed) _signal.MarkDriverFreed(id);
                throw;
            }
        }

        if (now >= _nextSweep)
        {
            _nextSweep = now + _timings.SweepEvery;
            foreach (var city in await _sweep.DueCitiesAsync(ct)) _signal.MarkDirty(city);
        }

        if (now >= _nextStuck)
        {
            _nextStuck = now + _timings.StuckEvery;
            var stuck = await _sweep.CountStuckAsync(ct);
            if (stuck > 0) _logger.LogWarning(TaxiRequestLogEvents.RequestStuck, "taxi.request.stuck {Count}", stuck);
        }

        var batch = _signal.TakeDirty(city => _heldBack.TryGetValue(city, out var until) && until > now);
        if (batch.Count > 0)
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = _timings.Parallelism, CancellationToken = ct },
                async (city, token) => await RunCityAsync(city, token));

        var after = _clock.GetUtcNow();
        var sleep = _nextSweep - after;
        if (_signal.PendingCities > 0 && _timings.LockBusyBackoff < sleep) sleep = _timings.LockBusyBackoff;
        return sleep > TimeSpan.Zero ? sleep : TimeSpan.Zero;
    }

    private async Task RunCityAsync(long city, CancellationToken ct)
    {
        for (var run = 0; ; run++)
        {
            MatcherPassResult result;
            try
            {
                result = await _pass.RunAsync(city, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(TaxiRequestLogEvents.MatchPassFailed, ex, "taxi.match.pass_failed {CityKey}", city);
                _heldBack[city] = _clock.GetUtcNow() + _timings.FailedPassBackoff;
                _signal.MarkDirty(city);
                return;
            }

            if (result.Outcome == MatcherPassOutcome.LockBusy)
            {
                _heldBack[city] = _clock.GetUtcNow() + _timings.LockBusyBackoff;
                return;
            }
            _heldBack.TryRemove(city, out _);
            if (run >= _timings.MaxInlineReruns || !_signal.TryTake(city)) return;
        }
    }
}

// Each pass in its own scope (its own DbContext and transaction).
public sealed class ScopedMatcherPass(IServiceScopeFactory scopes) : ITaxiMatcherPass
{
    public async Task<MatcherPassResult> RunAsync(long cityKey, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TaxiMatcherPass>().RunAsync(cityKey, ct);
    }
}

public sealed class PostgresMatcherSweepSource(IServiceScopeFactory scopes) : IMatcherSweepSource
{
    public Task<IReadOnlyList<long>> DueCitiesAsync(CancellationToken ct)
        => QueryAsync<long>(MatcherSql.DueCities, [], ct);

    public Task<IReadOnlyList<long>> CitiesOfDriversAsync(IReadOnlyCollection<Guid> driverListingIds, CancellationToken ct)
        => QueryAsync<long>(MatcherSql.CitiesOfDrivers,
            [new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = driverListingIds.ToArray() }], ct);

    public async Task<int> CountStuckAsync(CancellationToken ct)
        => (await QueryAsync<int>(MatcherSql.CountStuck, [], ct)).Single();

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object[] parameters, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Database.SqlQueryRaw<T>(sql, parameters).ToListAsync(ct);
    }
}
