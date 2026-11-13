using System.Text.Json;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using StackExchange.Redis;

namespace RentNearBy.Tests.Nearby;

// Home nearby "recently added" reads the shared Redis key first; a miss loads once and writes once.
public class HomeNearbyRecentCacheTests
{
    private sealed class Probe
    {
        public int Gets, Sets, Loads;
        public string? Written;
        public List<StayBrowseDto> Db = [new() { Id = Guid.NewGuid(), Name = "A" }, new() { Id = Guid.NewGuid(), Name = "B" }];
    }

    private static Task<List<StayBrowseDto>> Run(Probe p, Func<RedisValue> value, bool setThrows = false) =>
        HomeNearbyHandlers.RecentReadThroughAsync(
            () => { p.Gets++; return Task.FromResult(value()); },
            json => { p.Sets++; p.Written = json; return setThrows ? throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "down") : Task.CompletedTask; },
            () => { p.Loads++; return Task.FromResult(p.Db); });

    [Fact]
    public async Task Hit_returns_the_cached_list_in_order_without_the_database()
    {
        var p = new Probe();
        var cached = new List<StayBrowseDto> { new() { Id = Guid.NewGuid(), Name = "X" }, new() { Id = Guid.NewGuid(), Name = "Y" } };
        var r = await Run(p, () => JsonSerializer.Serialize(cached));
        Assert.Equal(cached.Select(x => x.Id), r.Select(x => x.Id));
        Assert.Equal(0, p.Loads);
        Assert.Equal(0, p.Sets);
    }

    [Fact]
    public async Task Miss_loads_once_and_writes_the_plain_json_once()
    {
        var p = new Probe();
        var r = await Run(p, () => RedisValue.Null);
        Assert.Same(p.Db, r);
        Assert.Equal(1, p.Loads);
        Assert.Equal(1, p.Sets);
        Assert.Equal(JsonSerializer.Serialize(p.Db), p.Written);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{not json")]
    [InlineData("7|[{}]")]
    public async Task Empty_null_or_corrupt_value_is_a_miss(string raw)
    {
        var p = new Probe();
        var r = await Run(p, () => raw);
        Assert.Same(p.Db, r);
        Assert.Equal(1, p.Loads);
        Assert.Equal(1, p.Sets);
    }

    [Fact]
    public async Task Redis_errors_fall_back_to_the_database()
    {
        var p = new Probe();
        var r = await Run(p, () => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"), setThrows: true);
        Assert.Same(p.Db, r);
        Assert.Equal(1, p.Loads);
        Assert.Equal(1, p.Sets);
    }
}
