using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Pattern of TripExecutionFeatureReader: fails closed (missing row = off = old map). Single api instance only.
public class MapDiscoveryFeatureReader(
    IMemoryCache cache, IServiceScopeFactory scopeFactory, ILogger<MapDiscoveryFeatureReader> logger)
    : IMapDiscoveryFeatureReader
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private long _version;

    public async Task<bool> IsEnabledAsync()
    {
        if (cache.TryGetValue(IMapDiscoveryFeatureReader.CacheKey, out bool cached)) return cached;

        var version = Interlocked.Read(ref _version);
        var enabled = await LoadAsync();

        if (version == Interlocked.Read(ref _version))
            cache.Set(IMapDiscoveryFeatureReader.CacheKey, enabled, Ttl);
        else
            logger.LogDebug("Map discovery load raced an admin change, result not cached");
        return enabled;
    }

    protected virtual async Task<bool> LoadAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.AppFeatureFlags.AsNoTracking()
            .Where(f => f.FeatureKey == AppFeatureKeys.MapDiscoveryEnabled)
            .Select(f => f.IsEnabled)
            .FirstOrDefaultAsync();
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        cache.Remove(IMapDiscoveryFeatureReader.CacheKey);
    }
}
