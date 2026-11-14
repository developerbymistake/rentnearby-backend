namespace RentNearBy.Core.Interfaces;

// Cached trip bookings master switch (IMemoryCache key config_trip_execution). A missing row means off.
public interface ITripExecutionFeatureReader
{
    public const string CacheKey = "config_trip_execution";

    Task<bool> IsEnabledAsync();
    void Invalidate();
}
