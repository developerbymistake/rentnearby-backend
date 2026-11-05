using System.Collections.Concurrent;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Trip city supply counts are keyed by district and a generation: a stay or taxi change bumps the district's generation
// (one INCR), so the next read misses and counts again. cityKey -> district is a point-in-polygon result kept on its own.
// Redis keeps the shared generation; a process counter also moves, so a lost INCR never serves a stale count here.
public static class TripSupplyCache
{
    public static readonly TimeSpan CountsTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan DistrictTtl = TimeSpan.FromHours(6);
    public const string NoDistrict = "-";
    public const string AllGenKey = "trip:supply:gen:all";

    private static long _localAll;
    private static readonly ConcurrentDictionary<Guid, long> LocalGen = new();

    public static string GenKey(Guid districtId) => $"trip:supply:gen:{districtId:N}";
    public static string DistrictKey(long cityKey) => $"trip:supply:city:{cityKey}";
    public static string CountsKey(Guid districtId, string version) => $"trip:supply:d:{districtId:N}:{version}";

    // After commit of any change that moves a stay or a rate-set driver in or out of the counts; null bumps every district.
    public static async Task BumpAsync(IConnectionMultiplexer? redis, Guid? districtId)
    {
        if (districtId is { } id) LocalGen.AddOrUpdate(id, 1, (_, v) => v + 1);
        else Interlocked.Increment(ref _localAll);
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().StringIncrementAsync(districtId is { } d ? GenKey(d) : AllGenKey);
        }
        catch
        {
        }
    }

    // null when Redis is configured but failed: the caller counts without the cache.
    public static async Task<string?> VersionAsync(IConnectionMultiplexer? redis, Guid districtId)
    {
        var local = $"{Interlocked.Read(ref _localAll)}.{(LocalGen.TryGetValue(districtId, out var v) ? v : 0)}";
        if (redis == null) return $"L{local}";
        try
        {
            var values = await redis.GetDatabase().StringGetAsync(new RedisKey[] { AllGenKey, GenKey(districtId) });
            return $"{Number(values[0])}.{Number(values[1])}-{local}";
        }
        catch
        {
            return null;
        }
    }

    private static string Number(RedisValue value) => value.HasValue ? value.ToString() : "0";
}
