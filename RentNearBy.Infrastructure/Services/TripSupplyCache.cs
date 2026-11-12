using System.Collections.Concurrent;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// City supply counts and the stay/taxi city lists are keyed by the city and ONE global generation: any stay or taxi change
// bumps it (one INCR), so the next read misses and loads again. The per-district generation is kept for the old district feeds.
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
    public static string CityCountsKey(long cityKey, string version) => $"trip:supply:c:{cityKey}:{version}";

    // After commit of any change that moves a stay or a rate-set driver in or out of the counts; null bumps every district.
    public static async Task BumpAsync(IConnectionMultiplexer? redis, Guid? districtId)
    {
        if (districtId is { } id) LocalGen.AddOrUpdate(id, 1, (_, v) => v + 1);
        Interlocked.Increment(ref _localAll);
        if (redis == null) return;
        try
        {
            var db = redis.GetDatabase();
            await db.StringIncrementAsync(AllGenKey);
            if (districtId is { } d) await db.StringIncrementAsync(GenKey(d));
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

    // Version of every city entry; null when Redis is configured but failed (the caller loads without the cache).
    public static async Task<string?> AllVersionAsync(IConnectionMultiplexer? redis)
    {
        var local = Interlocked.Read(ref _localAll);
        if (redis == null) return $"L{local}";
        try
        {
            return $"{Number(await redis.GetDatabase().StringGetAsync(AllGenKey))}-{local}";
        }
        catch
        {
            return null;
        }
    }

    private static string Number(RedisValue value) => value.HasValue ? value.ToString() : "0";
}
