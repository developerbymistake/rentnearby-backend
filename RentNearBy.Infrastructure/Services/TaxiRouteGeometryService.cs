using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Every 2 min (first pass after 30 s): road line of each public taxi route that has none, changed ends, or a retryable failure.
// OSRM only holds the six launch states and snaps an outside point to the nearest edge, so both ends must lie in one of them.
// Never called from a request. Skipped while OSRM is not configured.
public class TaxiRouteGeometryService(IServiceScopeFactory scopeFactory, ILogger<TaxiRouteGeometryService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RouteCacheTtl = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var osrm = sp.GetRequiredService<IOsrmClient>();
            if (!osrm.IsConfigured) return;

            var repo = sp.GetRequiredService<ITaxiRouteGeometryRepository>();
            var redis = sp.GetService<IConnectionMultiplexer>();
            var now = DateTime.UtcNow;
            var due = await repo.GetDueAsync(TaxiRouteGeometryRules.Batch, now.AddHours(-TaxiRouteGeometryRules.FailedRetryHours));
            if (due.Count == 0) return;

            int done = 0, failed = 0, uncovered = 0;
            var changed = false;
            foreach (var work in due)
            {
                if (ct.IsCancellationRequested) break;
                var row = await BuildAsync(work, repo, osrm, redis, ct);
                switch (row.Status)
                {
                    case TaxiRouteGeometryStatuses.Failed: failed++; break;
                    case TaxiRouteGeometryStatuses.Uncovered: uncovered++; break;
                    default: done++; break;
                }
                var same = work.Existing != null && work.Existing.EndsHash == row.EndsHash && work.Existing.Status == row.Status
                    && work.Existing.GeometryJson == row.GeometryJson;
                if (!same) changed = true;
                await repo.UpsertAsync(row);
            }

            if (changed) await TaxiWaysVersion.BumpAsync(redis);
            logger.LogInformation("taxi.geometry.pass {Done} {Failed} {Uncovered}", done, failed, uncovered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "taxi.geometry.pass failed");
        }
    }

    private async Task<TaxiRouteGeometry> BuildAsync(
        TaxiRouteGeometryWork work, ITaxiRouteGeometryRepository repo, IOsrmClient osrm, IConnectionMultiplexer? redis, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sameEnds = work.Existing?.EndsHash == work.EndsHash;
        var row = new TaxiRouteGeometry { RouteId = work.RouteId, EndsHash = work.EndsHash, UpdatedAt = now };

        // Same ends and a settled status: only the check time moves.
        if (sameEnds && work.Existing is { } e
            && e.Status is not TaxiRouteGeometryStatuses.Pending and not TaxiRouteGeometryStatuses.Failed)
        {
            row.Status = e.Status;
            row.GeometryJson = e.GeometryJson;
            row.DistanceKm = e.DistanceKm;
            row.Minutes = e.Minutes;
            return row;
        }

        var fromState = await repo.FindContainingStateAsync(work.FromLat, work.FromLng);
        var toState = await repo.FindContainingStateAsync(work.ToLat, work.ToLng);
        if (!OnboardingLaunchStates.Contains(fromState) || !OnboardingLaunchStates.Contains(toState))
        {
            row.Status = TaxiRouteGeometryStatuses.Uncovered;
            return row;
        }

        var points = new List<(double Lat, double Lon)> { (work.FromLat, work.FromLng), (work.ToLat, work.ToLng) };
        var route = await osrm.RouteAsync(points, ct);
        if (route == null)
        {
            row.Status = TaxiRouteGeometryStatuses.Failed;
            row.Attempts = (short)Math.Min(short.MaxValue, (sameEnds ? work.Existing!.Attempts : 0) + 1);
            return row;
        }

        var greatCircleKm = TaxiRouteGeometryRules.GreatCircleMetres(work.FromLat, work.FromLng, work.ToLat, work.ToLng) / 1000;
        row.DistanceKm = (decimal)Math.Round(route.Km, 1);
        row.Minutes = route.Minutes;
        if (TaxiRouteGeometryRules.IsSuspect(route.Km, greatCircleKm))
        {
            row.Status = TaxiRouteGeometryStatuses.Suspect;
            return row;
        }

        var raw = route.Geometry.GetRawText();
        var line = TaxiRouteGeometryRules.ParseLine(raw);
        if (line.Count < 2)
        {
            row.Status = TaxiRouteGeometryStatuses.NoRoute;
            return row;
        }

        row.Status = TaxiRouteGeometryStatuses.Ok;
        row.GeometryJson = TaxiRouteGeometryRules.ToGeoJson(TaxiRouteGeometryRules.Simplify(line));
        await CacheRouteAsync(redis, points, raw);
        return row;
    }

    // Same key the Trip planner reads, so one OSRM call serves both.
    private async Task CacheRouteAsync(IConnectionMultiplexer? redis, IReadOnlyList<(double Lat, double Lon)> points, string raw)
    {
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().StringSetAsync(TripCacheKeys.Route(points), raw, RouteCacheTtl);
        }
        catch (Exception ex)
        {
            logger.LogWarning("taxi.geometry cache write failed: {Error}", ex.Message);
        }
    }
}
