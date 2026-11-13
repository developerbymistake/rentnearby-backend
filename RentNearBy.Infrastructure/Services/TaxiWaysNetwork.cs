using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Callers keep the old snapshot while a newer one builds; only the very first build is awaited.
// The build runs on its own scope (the singleton cannot hold the pooled DbContext).
public class TaxiWaysNetwork(
    IServiceScopeFactory scopeFactory,
    IServiceProvider sp,
    IOptions<TaxiWaysOptions> options,
    ILogger<TaxiWaysNetwork> logger) : ITaxiWaysNetwork
{
    private readonly object _gate = new();
    private volatile TaxiWaysNetworkSnapshot? _current;
    private volatile IReadOnlyList<TripCityInfo>? _lastCities;
    private Task<TaxiWaysNetworkSnapshot?>? _building;
    private DateTime _lastFailureAt = DateTime.MinValue;
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CitiesRetry = TimeSpan.FromSeconds(60);

    public string? CurrentVersion => _current?.Version;

    public async Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        var version = await TaxiWaysVersion.TryReadAsync(redis);
        var current = _current;
        var safety = TimeSpan.FromMinutes(Math.Max(1, options.Value.RebuildSafetyMinutes));

        if (current != null)
        {
            var age = DateTime.UtcNow - current.BuiltAt;
            var stale = (version != null && version != current.Version) || age > safety || (current.CitiesMissing && age > CitiesRetry);
            if (stale && DateTime.UtcNow - _lastFailureAt > FailureBackoff) _ = StartBuild(version ?? current.Version);
            return current;
        }

        return await StartBuild(version ?? $"L{TaxiWaysVersion.Local}").WaitAsync(ct);
    }

    private Task<TaxiWaysNetworkSnapshot?> StartBuild(string version)
    {
        lock (_gate)
        {
            return _building ??= Task.Run(() => BuildAsync(version));
        }
    }

    // Second pass: route ends move to their parent city (Kathgodam end = Haldwani). The nearest-city snapshot serves until then;
    // it is replaced only if no newer build took its place meanwhile.
    private async Task RefineAsync(
        ParentCityResolver parent, TaxiWaysNetworkSnapshot first, IReadOnlyList<TaxiWaysSourceRow> rows,
        IReadOnlyList<TripCityInfo> cities, string version)
    {
        try
        {
            var ends = rows.SelectMany(r => new[] { (r.FromLat, r.FromLng), (r.ToLat, r.ToLng) }).Distinct().ToList();
            var parents = new System.Collections.Concurrent.ConcurrentDictionary<(double, double), TripCityInfo?>();
            await Parallel.ForEachAsync(ends, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                async (e, _) => parents[e] = await parent.ParentOfAsync(first, e.Item1, e.Item2));
            var refined = TaxiWaysClustering.Build(rows, cities, options.Value, version, first.BuiltAt, citiesMissing: false,
                endCity: (la, ln) => parents.GetValueOrDefault((la, ln)));
            lock (_gate)
            {
                if (ReferenceEquals(_current, first)) _current = refined;
            }
            logger.LogInformation("taxi.ways.parent.refined {Ends} {Places}", ends.Count, refined.Places.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning("taxi.ways.parent.refine failed: {Error}", ex.Message);
        }
    }

    private async Task<TaxiWaysNetworkSnapshot?> BuildAsync(string version)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            IReadOnlyList<TaxiWaysSourceRow> rows;
            using (var scope = scopeFactory.CreateScope())
            {
                rows = await scope.ServiceProvider.GetRequiredService<ITaxiWaysNetworkSource>().LoadAsync();
            }

            IReadOnlyList<TripCityInfo>? cities = null;
            try
            {
                cities = (await sp.GetRequiredService<ITripCityCatalog>().GetAsync())?.Cities;
            }
            catch (Exception ex)
            {
                logger.LogWarning("taxi.ways.atlas unavailable: {Error}", ex.Message);
            }

            // Atlas down: keep the last good city list so stays and local taxis never vanish; none yet = clusters only, retried soon.
            if (cities is { Count: > 0 }) _lastCities = cities;
            else cities = _lastCities;

            var snapshot = TaxiWaysClustering.Build(rows, cities, options.Value, version, DateTime.UtcNow, citiesMissing: cities == null);
            _current = snapshot;
            logger.LogInformation("taxi.ways.rebuild {Places} {Links} {Ms}", snapshot.Places.Count, snapshot.Links.Count, watch.ElapsedMilliseconds);
            if (cities != null && sp.GetService<ParentCityResolver>() is { } parent)
                _ = Task.Run(() => RefineAsync(parent, snapshot, rows, cities, version));
            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "taxi.ways.rebuild failed");
            _lastFailureAt = DateTime.UtcNow;
            return _current;
        }
        finally
        {
            lock (_gate) _building = null;
        }
    }
}
