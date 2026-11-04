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
    private Task<TaxiWaysNetworkSnapshot?>? _building;
    private DateTime _lastFailureAt = DateTime.MinValue;
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    public string? CurrentVersion => _current?.Version;

    public async Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        var version = await TaxiWaysVersion.TryReadAsync(redis);
        var current = _current;
        var safety = TimeSpan.FromMinutes(Math.Max(1, options.Value.RebuildSafetyMinutes));

        if (current != null)
        {
            var stale = (version != null && version != current.Version) || DateTime.UtcNow - current.BuiltAt > safety;
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
                logger.LogWarning("taxi.ways.atlas unavailable, clusters only: {Error}", ex.Message);
            }

            var snapshot = TaxiWaysClustering.Build(rows, cities, options.Value, version, DateTime.UtcNow);
            _current = snapshot;
            logger.LogInformation("taxi.ways.rebuild {Places} {Links} {Ms}", snapshot.Places.Count, snapshot.Links.Count, watch.ElapsedMilliseconds);
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
