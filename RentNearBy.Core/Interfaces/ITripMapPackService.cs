using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

// State map packs proxied from Atlas: versions per state and pre-gzipped bytes per version. No database.
public interface ITripMapPackService
{
    // Never throws: last known list, else empty.
    Task<IReadOnlyList<TripMapPackInfo>> ListAsync(CancellationToken ct = default);
    // (null, false) = not the current version or unknown state (404); (null, true) = Atlas down and nothing cached (503).
    Task<(TripMapPackBytes? Pack, bool Unavailable)> GetAsync(Guid stateId, string version, CancellationToken ct = default);
    // The next ListAsync refreshes from Atlas; the last known list is kept.
    void Invalidate();
}
