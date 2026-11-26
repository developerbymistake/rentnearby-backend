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
    public Task BustForUserAsync(Guid userId, string kind) => BustForUsersAsync([userId], kind);

    public async Task BustForUsersAsync(IReadOnlyCollection<Guid> userIds, string kind)
    {
        // Seeker plans (Bakhli Plus) cache nothing per listing.
        if (userIds.Count == 0 || kind is not (ListingKinds.Stay or ListingKinds.Taxi)) return;
        var redis = services.GetService<IConnectionMultiplexer>();
        if (redis == null)
        {
            if (kind == ListingKinds.Taxi) await TaxiWaysVersion.BumpAsync(null);
            return;
        }

        var ids = userIds.Distinct().ToList();
        List<Guid> districts;
        try
        {
            districts = kind switch
            {
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
            if (kind == ListingKinds.Taxi) await TaxiWaysVersion.BumpAsync(redis);
            return;
        }

        await BustDistrictsAsync(kind, districts);
    }

    public async Task BustDistrictsAsync(string kind, IEnumerable<Guid> districtIds)
    {
        var redis = services.GetService<IConnectionMultiplexer>();
        // Routes hidden or shown again move the taxi ways network too (the process counter even without Redis).
        if (kind == ListingKinds.Taxi) await TaxiWaysVersion.BumpAsync(redis);
        if (redis == null) return;

        try
        {
            var ids = districtIds.Distinct().ToList();
            var keys = ids.SelectMany(d => ListingCacheKeys.Keys(kind, d)).Distinct().Select(k => (RedisKey)k).ToArray();
            if (keys.Length > 0) await redis.GetDatabase().KeyDeleteAsync(keys);

            var genKeys = ids.SelectMany(d => ListingCacheKeys.GenKeys(kind, d))
                .Concat(ListingCacheKeys.GlobalGenKeys(kind))
                .Distinct()
                .ToList();
            await FeedCache.BumpAsync(redis, genKeys);

            // FB-37: plan rank and row changes also move the trip city lists and supply counts.
            foreach (var d in ids) await TripSupplyCache.BumpAsync(redis, d);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache bust failed for {Kind}", kind);
        }
    }
}
