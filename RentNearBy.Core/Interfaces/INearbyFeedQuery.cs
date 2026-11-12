using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface INearbyFeedQuery
{
    // take = pageSize + 1. Rows ordered by (distance asc, Id asc), strictly after `after`. No distance cutoff.
    Task<IReadOnlyList<NearbyRow>> PageAsync(NearbyKind kind, double lat, double lng, NearbyToken? after, int take, NearbyFilter filter, CancellationToken ct = default);
    // PriorityRank > 0 OR IsFeatured, within maxKm, order PriorityRank desc, IsFeatured desc, distance, Id. Stay and Taxi only (StayJob gives empty).
    Task<IReadOnlyList<NearbyRow>> FeaturedAsync(NearbyKind kind, double lat, double lng, double maxKm, int take, NearbyFilter filter, CancellationToken ct = default);
}
