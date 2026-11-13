using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Repositories;

namespace RentNearBy.Tests.Nearby;

// Each distinct stay point is resolved once; row order and the city of each row stay the same.
public class StayJobFeedCityResolveTests
{
    private sealed class CountingResolver : ICityResolver
    {
        public readonly List<(double Lat, double Lng)> Calls = [];
        public Exception? Throw;

        public double BoxKm => 10;

        public async Task<CityPin> ResolveAsync(double lat, double lng)
        {
            await Task.Yield();
            lock (Calls) Calls.Add((lat, lng));
            if (Throw != null) throw Throw;
            return new CityPin(true, new TripCityInfo { CityKey = (long)(lat * 1000), Name = $"C{lat:0.000}", StateName = "Uttarakhand" });
        }

        public Task<CityPin> FindAsync(long cityKey) => throw new NotSupportedException();
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey) => throw new NotSupportedException();
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    private static (StayJobFeedItemDto, decimal?, decimal?) Job(Guid id, decimal? lat, decimal? lng) =>
        (new StayJobFeedItemDto { Id = id, Title = "Cook" }, lat, lng);

    [Fact]
    public async Task Duplicate_points_are_resolved_once_and_every_row_keeps_its_order_and_city()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var rows = ids.Select((id, i) => new NearbyRow(id, (i + 1) * 1000.0)).ToList();
        var loaded = new List<(StayJobFeedItemDto, decimal?, decimal?)>
        {
            Job(ids[3], 29.2m, 79.5m), Job(ids[0], 29.2m, 79.5m), Job(ids[1], 29.30m, 79.40m),
            Job(ids[2], 29.2000m, 79.5000m), Job(ids[4], null, null), Job(ids[5], 29.3m, 79.4m),
        };
        var resolver = new CountingResolver();

        var result = await StayJobFeedHydrator.AssembleAsync(rows, loaded, resolver, "Haldwani");

        Assert.Equal(2, resolver.Calls.Count);
        Assert.Equal(ids, result.Keys);
        Assert.Equal("C29.200", result[ids[0]].CityName);
        Assert.Equal("C29.200", result[ids[2]].CityName);
        Assert.Equal("C29.200", result[ids[3]].CityName);
        Assert.Equal("C29.300", result[ids[1]].CityName);
        Assert.Equal("C29.300", result[ids[5]].CityName);
        Assert.Null(result[ids[4]].CityName);
        Assert.Null(result[ids[4]].CityKey);
        Assert.Equal(NearbyRules.StayKm(1000), result[ids[0]].DistanceKm);
    }

    [Fact]
    public async Task Missing_ids_are_skipped_and_no_coordinates_means_no_resolve_call()
    {
        var a = Guid.NewGuid();
        var resolver = new CountingResolver();
        var result = await StayJobFeedHydrator.AssembleAsync(
            [new NearbyRow(Guid.NewGuid(), 500), new NearbyRow(a, 900)], [Job(a, null, 79.5m)], resolver, null);

        Assert.Empty(resolver.Calls);
        Assert.Equal([a], result.Keys);
        Assert.Null(result[a].CityName);
    }

    [Fact]
    public async Task A_resolver_failure_still_propagates()
    {
        var a = Guid.NewGuid();
        var resolver = new CountingResolver { Throw = new InvalidOperationException("boom") };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StayJobFeedHydrator.AssembleAsync([new NearbyRow(a, 900)], [Job(a, 29.2m, 79.5m)], resolver, null));
    }
}
