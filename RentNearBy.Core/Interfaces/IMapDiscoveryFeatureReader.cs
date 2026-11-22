namespace RentNearBy.Core.Interfaces;

// Cached new trip map switch (IMemoryCache key config_map_discovery). A missing row means off (old map).
public interface IMapDiscoveryFeatureReader
{
    public const string CacheKey = "config_map_discovery";

    Task<bool> IsEnabledAsync();
    void Invalidate();
}
