using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface INearbyContext
{
    Task<NearbyHeaderDto> HeaderAsync(double lat, double lng, double? nearestKm, CancellationToken ct = default);
    // Nearest live Atlas city, no distance cutoff, scan of the cached catalog. null when the catalog is unavailable.
    Task<NearbyCity?> NearestCityAsync(double lat, double lng, CancellationToken ct = default);
}
