using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Reads the generation counters of ListingCacheKeys; a missing or unreadable counter is 0.
public static class ListingCacheGeneration
{
    public static async Task<long> ReadAsync(IDatabase db, string genKey)
        => (await ReadManyAsync(db, [genKey]))[0];

    public static async Task<long[]> ReadManyAsync(IDatabase db, IReadOnlyList<string> genKeys)
    {
        var result = new long[genKeys.Count];
        if (genKeys.Count == 0) return result;
        var values = await db.StringGetAsync(genKeys.Select(k => (RedisKey)k).ToArray());
        for (var i = 0; i < values.Length; i++)
            result[i] = values[i].TryParse(out long n) ? n : 0;
        return result;
    }
}
