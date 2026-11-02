using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

// The backend's copy of Atlas's live trip city list.
public interface ITripCityCatalog
{
    // null = Atlas unreachable and no copy (fresh or stale) anywhere.
    Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default);
    // (false, null) when GetAsync is null; (true, null) when the key is not live.
    Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default);
    Task InvalidateAsync();
}
