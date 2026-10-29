using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Best-effort, after commit: short TTLs cover a Redis failure. Redis is optional (REDIS_URL unset = no-op).
public class ListingCacheInvalidator(ApplicationDbContext db, IServiceProvider services, ILogger<ListingCacheInvalidator> logger)
    : IListingCacheInvalidator
{
    private const int ScanPageSize = 1000;
    private const int DeleteChunk = 500;

    public Task BustForUserAsync(Guid userId, string kind) => BustForUsersAsync([userId], kind);

    public async Task BustForUsersAsync(IReadOnlyCollection<Guid> userIds, string kind)
    {
        if (userIds.Count == 0 || services.GetService<IConnectionMultiplexer>() == null) return;

        var ids = userIds.Distinct().ToList();
        List<Guid> districts;
        try
        {
            districts = kind switch
            {
                ListingKinds.Room => await db.RoomListings.AsNoTracking()
                    .Where(l => ids.Contains(l.UserId) && !l.IsDeleted).Select(l => l.DistrictId).Distinct().ToListAsync(),
                ListingKinds.Plot => await db.PlotListings.AsNoTracking()
                    .Where(l => ids.Contains(l.UserId) && !l.IsDeleted).Select(l => l.DistrictId).Distinct().ToListAsync(),
                ListingKinds.Stay => await db.StayListings.AsNoTracking()
                    .Where(s => ids.Contains(s.UserId) && !s.IsDeleted && s.DistrictId != null)
                    .Select(s => s.DistrictId!.Value).Distinct().ToListAsync(),
                ListingKinds.Taxi => await db.TaxiListings.AsNoTracking()
                    .Where(t => ids.Contains(t.UserId) && !t.IsDeleted && t.DistrictId != null)
                    .Select(t => t.DistrictId!.Value).Distinct().ToListAsync(),
                _ => [],
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache bust: could not read districts for {Count} users ({Kind})", ids.Count, kind);
            return;
        }

        await BustDistrictsAsync(kind, districts);
    }

    public async Task BustDistrictsAsync(string kind, IEnumerable<Guid> districtIds)
    {
        var redis = services.GetService<IConnectionMultiplexer>();
        if (redis == null) return;

        try
        {
            var cache = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            var ids = districtIds.Distinct().ToList();

            var keys = ids.SelectMany(d => ListingCacheKeys.Keys(kind, d)).Distinct().Select(k => (RedisKey)k).ToArray();
            if (keys.Length > 0) await cache.KeyDeleteAsync(keys);

            if (server == null) return;
            var patterns = ids.SelectMany(d => ListingCacheKeys.Patterns(kind, d))
                .Concat(ListingCacheKeys.GlobalPatterns(kind))
                .Distinct()
                .ToList();
            if (patterns.Count == 0) return;

            // Every pattern is "<prefix>*": one keyspace SCAN, prefix-matched here, deleted in chunks.
            var prefixes = patterns.Select(p => p[..^1]).ToArray();
            var matched = new List<RedisKey>();
            await foreach (var key in server.KeysAsync(pattern: CommonScanPattern(prefixes), pageSize: ScanPageSize))
            {
                var k = key.ToString();
                if (!prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))) continue;
                matched.Add(key);
                if (matched.Count >= DeleteChunk)
                {
                    await cache.KeyDeleteAsync(matched.ToArray());
                    matched.Clear();
                }
            }
            if (matched.Count > 0) await cache.KeyDeleteAsync(matched.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache bust failed for {Kind}", kind);
        }
    }

    // Longest shared prefix narrows the server-side MATCH; glob metacharacters stop it.
    private static string CommonScanPattern(string[] prefixes)
    {
        var common = prefixes[0];
        foreach (var p in prefixes.Skip(1))
        {
            var n = 0;
            while (n < common.Length && n < p.Length && common[n] == p[n]) n++;
            common = common[..n];
        }
        var stop = common.IndexOfAny(['*', '?', '[', ']', '\\']);
        if (stop >= 0) common = common[..stop];
        return common + "*";
    }
}
