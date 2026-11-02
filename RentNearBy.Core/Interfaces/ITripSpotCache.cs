using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripSpotCache
{
    // Read-through; null = nothing available (Atlas down and no stale copy).
    Task<CachedCitySpots?> GetAsync(long cityKey, CancellationToken ct = default);
    Task InvalidateAsync(long cityKey);
}
