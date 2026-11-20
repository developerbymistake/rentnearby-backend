using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

public sealed record TaxiRouteBackfillResult(int Batches, int Written, int Unresolved, int FailedBatches, int RecheckedDrivers = 0);

// One pass over routes with a null city key: keyset batches, a short pause between them, never a guessed key. A driver
// with a null end that got a final answer in this pass (a key, or no city) has his ringing and accepted offers checked
// again at the end (O12: a route save that could not wait for the lookup kept them).
public sealed class TaxiRouteCityBackfillPass(
    ITaxiRouteCityKeyStore store, ICityResolver resolver, ILogger logger, Func<TimeSpan, CancellationToken, Task>? pause = null)
{
    public const int BatchSize = 100;
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);
    // Between two city lookups (each may be one reverse-geocoder call): at most 5 a second.
    public static readonly TimeSpan LookupPause = TimeSpan.FromMilliseconds(200);

    private readonly Func<TimeSpan, CancellationToken, Task> _pause = pause ?? Task.Delay;

    public async Task<TaxiRouteBackfillResult> RunAsync(CancellationToken ct)
    {
        var cache = new TaxiRouteCityKeys.PointCache(resolver, c => _pause(LookupPause, c));
        var after = Guid.Empty;
        int batches = 0, written = 0, unresolved = 0, failed = 0, rechecked = 0;
        var settledDrivers = new HashSet<Guid>();
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<TaxiRouteKeyRow> rows;
            try
            {
                rows = await store.NextMissingAsync(after, BatchSize, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "taxi.route.city.backfill read failed after {After}", after);
                break;
            }
            if (rows.Count == 0) break;
            after = rows[^1].Id;
            batches++;
            try
            {
                var (writes, missing) = await TaxiRouteCityKeys.ResolveRowsAsync(cache, rows, ct);
                unresolved += missing;
                written += await store.WriteAsync(writes, ct);
                foreach (var row in rows)
                    if ((row.FromCityKey is null && cache.IsSettled(row.FromLatitude, row.FromLongitude))
                        || (row.ToCityKey is null && cache.IsSettled(row.ToLatitude, row.ToLongitude)))
                        settledDrivers.Add(row.TaxiListingId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "taxi.route.city.backfill batch failed after {After}", after);
            }
            if (rows.Count < BatchSize) break;
            await _pause(Pause, ct);
        }
        if (unresolved > 0)
            logger.LogWarning(TaxiRouteCityKeys.Unresolved, "taxi.route.city.unresolved {Count} routes in this pass", unresolved);
        if (settledDrivers.Count > 0 && !ct.IsCancellationRequested)
        {
            try
            {
                rechecked = await store.RecheckOffersAsync(settledDrivers, (lat, lng) => !cache.IsSettled(lat, lng), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "taxi.route.city.backfill offer recheck failed");
            }
        }
        return new TaxiRouteBackfillResult(batches, written, unresolved, failed, rechecked);
    }
}

// Two minutes after start, then nightly at 00:20 IST, under one advisory lock across instances.
public sealed class TaxiRouteCityBackfillService(
    ITaxiRouteCityKeyStore store, ICityResolver resolver, DbLockConnectionString lockConnection, ILogger<TaxiRouteCityBackfillService> logger)
    : BackgroundService
{
    public const string LockName = "taxi-route-city-backfill";
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    public const int NightlyHourIst = 0;
    public const int NightlyMinuteIst = 20;

    public static DateTime NextNightlyUtc(DateTime nowUtc)
    {
        var today = IstTime.TodayIst(nowUtc);
        var at = IstTime.AtUtc(today, NightlyHourIst, NightlyMinuteIst);
        return at > nowUtc ? at : IstTime.AtUtc(today.AddDays(1), NightlyHourIst, NightlyMinuteIst);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunGuardedAsync(
                    async ct => await PgAdvisoryLock.TryAcquireAsync(lockConnection.Value, LockName, ct),
                    ct => new TaxiRouteCityBackfillPass(store, resolver, logger).RunAsync(ct),
                    logger, stoppingToken);
                await Task.Delay(Every, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Nothing escapes: a lock or pass failure is logged and the next run tries again. Null = skipped or failed.
    public static async Task<TaxiRouteBackfillResult?> RunGuardedAsync(
        Func<CancellationToken, Task<IAsyncDisposable?>> acquireLock,
        Func<CancellationToken, Task<TaxiRouteBackfillResult>> pass,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            await using var gate = await acquireLock(ct);
            if (gate is null)
            {
                logger.LogInformation("taxi.route.city.backfill skipped: lock busy");
                return null;
            }
            var result = await pass(ct);
            logger.LogInformation("taxi.route.city.backfill {Batches} batches, {Written} written, {Unresolved} unresolved, {Failed} failed",
                result.Batches, result.Written, result.Unresolved, result.FailedBatches);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "taxi.route.city.backfill run failed");
            return null;
        }
    }
}
